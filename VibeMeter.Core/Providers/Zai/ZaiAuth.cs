using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VibeMeter.Providers.Zai;

/// <summary>
/// Detects the local Z.ai / GLM coding setup. The Z.ai coding plan is consumed
/// via an API key, which may live in any of:
///   - the <c>ZAI_API_KEY</c> / <c>ANTHROPIC_AUTH_TOKEN</c> env vars,
///   - a supported coding CLI's config file (see below), or
///   - a <c>%USERPROFILE%\.zai\</c> config file (legacy).
///
/// Supported CLIs (probed in priority order):
///   - ZCode: <c>%USERPROFILE%\.zcode\v2\config.json</c> — reads the
///     <c>apiKey</c> of the enabled <c>builtin:zai-*</c> provider entry.
///   - opencode: <c>%USERPROFILE%\.local\share\opencode\auth.json</c> — reads
///     the <c>zai-coding-plan.key</c> entry.
///
/// Note: ZCode's OAuth token (<c>credentials.json</c>) is scoped to the
/// <c>/api/anthropic</c> endpoint and is rejected by the monitor API, so it is
/// intentionally not used here.
/// </summary>
public sealed class ZaiAuth
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Env vars that indicate a Z.ai key has been configured.</summary>
    private static readonly string[] KeyEnvVars = { "ZAI_API_KEY", "ANTHROPIC_AUTH_TOKEN" };

    /// <summary>Possible Z.ai CLI config locations (legacy <c>.zai</c> folder).</summary>
    private static readonly string[] ConfigFiles =
    {
        Path.Combine(HomePath, ".zai", "user-settings.json"),
        Path.Combine(HomePath, ".zai", "config.json"),
        Path.Combine(HomePath, ".zai", "settings.json")
    };

    /// <summary>ZCode config — holds per-provider apiKey entries.</summary>
    private static readonly string ZcodeConfigPath =
        Path.Combine(HomePath, ".zcode", "v2", "config.json");

    /// <summary>opencode auth — holds per-provider API keys.</summary>
    private static readonly string OpencodeAuthPath =
        Path.Combine(HomePath, ".local", "share", "opencode", "auth.json");

    /// <summary>True when a Z.ai API key or CLI config is present on this PC.</summary>
    public bool IsConfigured => GetApiKey() is not null || ConfigFiles.Any(File.Exists);

    /// <summary>The resolved API key (never logged), or null when absent.</summary>
    public string? GetApiKey() =>
        FromEnvVars()
        ?? FromZcodeConfig()
        ?? FromOpencodeAuth()
        ?? FromLegacyZaiFiles();

    /// <summary>A short, non-sensitive description of where Z.ai was detected.</summary>
    public string DetectionLabel =>
        GetApiKey() is not null
            ? SourceLabel()
            : ConfigFiles.FirstOrDefault(File.Exists) is { } f
                ? Path.GetFileName(Path.GetDirectoryName(f)!) + "/" + Path.GetFileName(f)
                : "not found";

    private string? FromEnvVars() =>
        KeyEnvVars
            .Select(v => Environment.GetEnvironmentVariable(v))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Reads ZCode's <c>config.json</c> and returns the apiKey of the first
    /// <c>builtin:zai-*</c> provider that is both enabled and has a non-empty key.
    /// Falls back to any <c>builtin:zai-*</c> entry with a key if none is enabled.
    /// </summary>
    private string? FromZcodeConfig()
    {
        if (!File.Exists(ZcodeConfigPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(ZcodeConfigPath));
            if (!doc.RootElement.TryGetProperty("provider", out var providers)) return null;

            string? fallback = null;
            foreach (var prop in providers.EnumerateObject())
            {
                if (!prop.Name.StartsWith("builtin:zai", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                string? key = GetZcodeApiKey(prop.Value);
                if (string.IsNullOrWhiteSpace(key)) continue;

                // Prefer an enabled entry; remember the first key as a fallback.
                bool enabled = prop.Value.TryGetProperty("enabled", out var e)
                               && e.ValueKind == JsonValueKind.True;
                if (enabled) return key;
                fallback ??= key;
            }
            return fallback;
        }
        catch
        {
            // Malformed config or IO race — treat as "no key here".
            return null;
        }
    }

    private static string? GetZcodeApiKey(JsonElement provider)
    {
        if (!provider.TryGetProperty("options", out var opts)) return null;
        if (!opts.TryGetProperty("apiKey", out var keyProp)) return null;
        return keyProp.ValueKind == JsonValueKind.String
            ? keyProp.GetString()
            : null;
    }

    /// <summary>Reads opencode's <c>auth.json</c> <c>zai-coding-plan.key</c> entry.</summary>
    private string? FromOpencodeAuth()
    {
        if (!File.Exists(OpencodeAuthPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(OpencodeAuthPath));
            if (!doc.RootElement.TryGetProperty("zai-coding-plan", out var entry)) return null;
            if (entry.ValueKind != JsonValueKind.Object) return null;
            if (!entry.TryGetProperty("key", out var keyProp)) return null;
            return keyProp.ValueKind == JsonValueKind.String
                ? keyProp.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last-resort: scan the legacy <c>.zai</c> JSON files for an apiKey field.</summary>
    private string? FromLegacyZaiFiles()
    {
        foreach (var path in ConfigFiles)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("apiKey", out var keyProp)
                    && keyProp.ValueKind == JsonValueKind.String)
                {
                    return keyProp.GetString();
                }
            }
            catch
            {
                // skip unreadable file
            }
        }
        return null;
    }

    /// <summary>Reports which source supplied the key (parallel to the GetApiKey probe order).</summary>
    private string SourceLabel()
    {
        if (FromEnvVars() is not null) return "env var";
        if (FromZcodeConfig() is not null) return "zcode";
        if (FromOpencodeAuth() is not null) return "opencode";
        return ".zai config";
    }
}
