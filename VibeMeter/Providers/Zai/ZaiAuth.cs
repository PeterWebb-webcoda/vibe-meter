using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VibeMeter.Providers.Zai;

/// <summary>
/// Detects the local Z.ai / GLM coding setup. Z.ai's coding plan is consumed either
/// via an API key (the <c>ZAI_API_KEY</c> env var is the canonical signal) or through
/// a supported coding CLI that writes <c>%USERPROFILE%\.zai\</c>.
/// </summary>
public sealed class ZaiAuth
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Env vars that indicate a Z.ai key has been configured.</summary>
    private static readonly string[] KeyEnvVars = { "ZAI_API_KEY", "ANTHROPIC_AUTH_TOKEN" };

    /// <summary>Possible Z.ai CLI config locations.</summary>
    private static readonly string[] ConfigFiles =
    {
        Path.Combine(HomePath, ".zai", "user-settings.json"),
        Path.Combine(HomePath, ".zai", "config.json"),
        Path.Combine(HomePath, ".zai", "settings.json")
    };

    /// <summary>True when a Z.ai API key or CLI config is present on this PC.</summary>
    public bool IsConfigured => GetApiKey() is not null || ConfigFiles.Any(File.Exists);

    /// <summary>The resolved API key (never logged), or null when absent.</summary>
    public string? GetApiKey() =>
        KeyEnvVars
            .Select(v => Environment.GetEnvironmentVariable(v))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>A short, non-sensitive description of where Z.ai was detected.</summary>
    public string DetectionLabel =>
        GetApiKey() is not null
            ? "API key"
            : ConfigFiles.FirstOrDefault(File.Exists) is { } f
                ? Path.GetFileName(Path.GetDirectoryName(f)!) + "/" + Path.GetFileName(f)
                : "not found";
}
