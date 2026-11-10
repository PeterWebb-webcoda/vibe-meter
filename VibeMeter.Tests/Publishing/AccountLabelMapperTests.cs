using System.Text.Json;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The optional account fields (<c>accountLabel</c>, <c>accountKey</c>) and the email
/// scrub. The deployed API rejects unknown members with a 400, so the overriding rule is
/// that an unconfigured mapper emits the exact document it always did. Every identifier
/// and address here is a made-up value.
/// </summary>
public sealed class AccountLabelMapperTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    private const string FakeAccountUuid = "11111111-2222-3333-4444-555555555555";
    private const string FakeOrgUuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string FakeEmail = "someone.fake@example.test";

    /// <summary>The pre-account-split document for <see cref="Claude"/>, byte for byte.</summary>
    private const string GoldenJson =
        """{"schemaVersion":1,"observedAt":"2026-09-17T04:30:00+00:00","providers":[{"providerId":"claude","state":"available","planLabel":"Claude Max 5x","gauges":[{"id":"five-hour","title":"5h","subtitle":null,"percentRemaining":58,"resetAt":null,"resetWindowSeconds":null}]}]}""";

    // ------------------------------------------------------------ unconfigured

    [Fact]
    public void Unconfigured_EmitsTheExactPreviousDocument_EvenWhenTheProviderHasAKey()
    {
        foreach (var mapper in new[] { new SnapshotMapper(), new SnapshotMapper(null), new SnapshotMapper(AccountLabels.None) })
        {
            var json = mapper.Map([Claude(accountKey: ClaudeKey())], FixedUtcNow).Json;

            Assert.Equal(GoldenJson, json);
            Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Unconfigured_AndBlankLabels_EmitNoAccountFields()
    {
        var labels = AccountLabels.Create("  ", new Dictionary<string, string> { ["codex"] = "" });

        var json = new SnapshotMapper(labels).Map([Claude(), Usage("codex")], FixedUtcNow).Json;

        Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(labels.Warnings);
    }

    // ------------------------------------------------------------ configured

    [Fact]
    public void Configured_EmitsLabelAndKey_AfterTheExistingFields()
    {
        var key = ClaudeKey();
        var mapped = new SnapshotMapper(AccountLabels.Create("webcoda", null)).Map([Claude(accountKey: key)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var provider = document.RootElement.GetProperty("providers")[0];

        Assert.Equal(
            ["providerId", "state", "planLabel", "gauges", "accountLabel", "accountKey"],
            provider.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("webcoda", provider.GetProperty("accountLabel").GetString());
        Assert.Equal(key, provider.GetProperty("accountKey").GetString());
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void Configured_WithNoIdentifier_SendsTheLabelAlone()
    {
        var json = new SnapshotMapper(AccountLabels.Create("webcoda", null)).Map([Usage("zai")], FixedUtcNow).Json;

        using var document = JsonDocument.Parse(json);
        var provider = document.RootElement.GetProperty("providers")[0];

        Assert.Equal("webcoda", provider.GetProperty("accountLabel").GetString());
        Assert.False(provider.TryGetProperty("accountKey", out _));
    }

    [Fact]
    public void PerProviderOverride_WinsOverTheDefault_AndCanStandAlone()
    {
        var withDefault = AccountLabels.Create("webcoda", new Dictionary<string, string> { ["Codex"] = "shared" });
        var overrideOnly = AccountLabels.Parse(null, "codex=shared");

        var mapped = Providers(new SnapshotMapper(withDefault).Map([Claude(), Usage("codex")], FixedUtcNow));
        Assert.Equal("webcoda", mapped["claude"].GetProperty("accountLabel").GetString());
        Assert.Equal("shared", mapped["codex"].GetProperty("accountLabel").GetString());

        var only = Providers(new SnapshotMapper(overrideOnly).Map([Claude(), Usage("codex")], FixedUtcNow));
        Assert.False(only["claude"].TryGetProperty("accountLabel", out _));
        Assert.False(only["claude"].TryGetProperty("accountKey", out _));
        Assert.Equal("shared", only["codex"].GetProperty("accountLabel").GetString());
    }

    [Theory]
    [InlineData("Webcoda")]
    [InlineData("9lives")]
    [InlineData("-work")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    public void InvalidLabel_IsIgnoredWithAWarning_AndPublishingContinues(string invalid)
    {
        var labels = AccountLabels.Create(invalid, null);

        Assert.Null(labels.Default);
        Assert.Single(labels.Warnings);

        var mapped = new SnapshotMapper(labels).Map([Claude(accountKey: ClaudeKey())], FixedUtcNow);
        Assert.Equal(GoldenJson, mapped.Json);
    }

    [Fact]
    public void InvalidOverride_FallsBackToTheDefault_AndMalformedEntriesAreWarned()
    {
        var labels = AccountLabels.Parse("webcoda", "codex=Shared;nonsense; =x");

        Assert.Equal("webcoda", labels.For("codex"));
        Assert.Equal(3, labels.Warnings.Count);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("webcoda")]
    [InlineData("personal-2")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // 32 characters
    public void ValidLabels_AreAccepted(string label)
    {
        Assert.True(AccountLabels.IsValid(label));
        Assert.Equal(label, AccountLabels.Create($" {label} ", null).Default);
    }

    [Fact]
    public void MalformedProviderKey_IsDroppedWithoutQuotingIt()
    {
        var mapped = new SnapshotMapper(AccountLabels.Create("webcoda", null))
            .Map([Claude(accountKey: FakeAccountUuid)], FixedUtcNow);

        Assert.DoesNotContain("accountKey", mapped.Json);
        Assert.Contains("\"accountLabel\":\"webcoda\"", mapped.Json);
        Assert.DoesNotContain(mapped.Warnings, w => w.Contains(FakeAccountUuid));
        Assert.DoesNotContain(FakeAccountUuid, mapped.Json);
    }

    [Fact]
    public void ChangingTheLabel_ChangesTheIdempotencyKey()
    {
        string KeyFor(AccountLabels labels) =>
            IdempotencyKey.For(new SnapshotMapper(labels).Map([Claude(accountKey: ClaudeKey())], FixedUtcNow).Json);

        var none = KeyFor(AccountLabels.None);
        var work = KeyFor(AccountLabels.Create("webcoda", null));
        var personal = KeyFor(AccountLabels.Create("personal", null));

        Assert.Equal(work, KeyFor(AccountLabels.Create("webcoda", null)));
        Assert.Equal(3, new[] { none, work, personal }.Distinct().Count());
    }

    // ------------------------------------------------------------ the key itself

    [Fact]
    public void AccountKey_IsDeterministic16LowerHex_AndDiffersPerProviderAndAccount()
    {
        var claude = AccountKey.Derive("claude", FakeOrgUuid, FakeAccountUuid);

        Assert.Equal(claude, AccountKey.Derive("claude", FakeOrgUuid, FakeAccountUuid));
        Assert.Equal(claude, AccountKey.Derive("claude", FakeOrgUuid.ToUpperInvariant(), $" {FakeAccountUuid} "));
        Assert.Matches("^[0-9a-f]{16}$", claude);
        Assert.True(AccountKey.IsValid(claude));

        Assert.NotEqual(claude, AccountKey.Derive("codex", FakeOrgUuid, FakeAccountUuid));
        Assert.NotEqual(claude, AccountKey.Derive("claude", FakeOrgUuid, "11111111-2222-3333-4444-555555555556"));
        Assert.NotEqual(claude, AccountKey.Derive("claude", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeef", FakeAccountUuid));
        Assert.NotEqual(AccountKey.Derive("claude", "ab", "c"), AccountKey.Derive("claude", "a", "bc"));

        Assert.Null(AccountKey.Derive("claude"));
        Assert.Null(AccountKey.Derive("claude", null, " "));
    }

    [Fact]
    public void ClaudeKey_ComesFromTheOauthAccountUuids_AndNeedsAnAccountUuid()
    {
        var account = new ClaudeOAuthAccount { AccountUuid = FakeAccountUuid, OrganizationUuid = FakeOrgUuid, EmailAddress = FakeEmail };

        Assert.Equal(AccountKey.Derive("claude", FakeOrgUuid, FakeAccountUuid), ClaudeProvider.DeriveAccountKey(account));
        Assert.Null(ClaudeProvider.DeriveAccountKey(new ClaudeOAuthAccount { OrganizationUuid = FakeOrgUuid }));
        Assert.Null(ClaudeProvider.DeriveAccountKey(null));
    }

    [Fact]
    public async Task CodexKey_ComesFromTheAuthFilesAccountId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vibemeter-codex-auth-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, $$$"""{"tokens":{"access_token":"not-a-real-token","account_id":"{{{FakeAccountUuid}}}"}}""");
            Assert.Equal(AccountKey.Derive("codex", FakeAccountUuid), await CodexAuth.ReadAccountKeyAsync(path));

            await File.WriteAllTextAsync(path, """{"tokens":{"access_token":"not-a-real-token"}}""");
            Assert.Null(await CodexAuth.ReadAccountKeyAsync(path));

            await File.WriteAllTextAsync(path, "{ not json");
            Assert.Null(await CodexAuth.ReadAccountKeyAsync(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(await CodexAuth.ReadAccountKeyAsync(path));
    }

    [Fact]
    public void NoRawIdentifierOrEmail_AppearsAnywhereInTheDocument()
    {
        var labels = AccountLabels.Create("webcoda", null);
        var mapped = new SnapshotMapper(labels).Map(
        [
            Claude(accountKey: ClaudeKey()),
            Usage("codex", accountKey: AccountKey.Derive("codex", FakeAccountUuid)),
            Usage("google", planLabel: $"Gemini Pro — {FakeEmail}"),
        ],
            FixedUtcNow);

        foreach (var raw in new[] { FakeAccountUuid, FakeOrgUuid, FakeEmail, "@example.test", "someone.fake" })
        {
            Assert.DoesNotContain(raw, mapped.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(mapped.Warnings, w => w.Contains(raw, StringComparison.OrdinalIgnoreCase));
        }

        var providers = Providers(mapped);
        Assert.Equal(3, providers.Values.Select(p => p.GetProperty("accountKey").GetString()).Distinct().Count());
    }

    // ------------------------------------------------------------ email scrub

    [Theory]
    [InlineData("Gemini Pro — someone.fake@example.test", "Gemini Pro")]
    [InlineData("someone.fake@example.test", null)]
    [InlineData("Free (someone.fake@example.test)", "Free")]
    [InlineData("Gemini Pro", "Gemini Pro")]
    public void GooglePlanLabel_HasItsEmailStripped_WhenPublished(string displayed, string? published)
    {
        var usage = Usage("google", planLabel: displayed);

        var mapped = new SnapshotMapper().Map([usage], FixedUtcNow);

        Assert.Equal(published, mapped.Request.Providers![0].PlanLabel);
        Assert.DoesNotContain("@", mapped.Json);

        // The card's copy is untouched: only the wire loses the address.
        Assert.Equal(displayed, usage.PlanLabel);
    }

    [Fact]
    public void GoogleEmail_BecomesTheAccountKey_OnlyWhenLabelled()
    {
        var usage = Usage("google", planLabel: $"Gemini Pro — {FakeEmail}");

        var unlabelled = new SnapshotMapper().Map([usage], FixedUtcNow).Json;
        Assert.DoesNotContain("account", unlabelled, StringComparison.OrdinalIgnoreCase);

        var labelled = Providers(new SnapshotMapper(AccountLabels.Create("personal", null)).Map([usage], FixedUtcNow));
        Assert.Equal(AccountKey.Derive("google", FakeEmail), labelled["google"].GetProperty("accountKey").GetString());
    }

    // ------------------------------------------------------------ helpers

    private static string ClaudeKey() => AccountKey.Derive("claude", FakeOrgUuid, FakeAccountUuid)!;

    private static Dictionary<string, JsonElement> Providers(MappedSnapshot mapped) =>
        JsonDocument.Parse(mapped.Json).RootElement.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("providerId").GetString()!, p => p.Clone());

    private static ProviderUsage Claude(string? accountKey = null) => new()
    {
        ProviderId = "claude",
        DisplayName = "Claude Code",
        State = ProviderState.Ok,
        PlanLabel = "Claude Max 5x",
        Gauges = [new UsageGauge("five-hour", "5h", null, 58, null)],
        AccountKey = accountKey,
    };

    private static ProviderUsage Usage(string id, string? planLabel = null, string? accountKey = null) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        PlanLabel = planLabel,
        AccountKey = accountKey,
    };
}
