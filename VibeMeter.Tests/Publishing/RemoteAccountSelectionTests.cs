using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// Which read-back accounts count as "someone else's", and how old a reading may be
/// before it is marked stale or dropped. Synthetic labels and identifiers throughout.
/// </summary>
public sealed class RemoteAccountSelectionTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly AccountLabels LocalWork = AccountLabels.Create("work", null);

    [Fact]
    public void ThisMachinesOwnLabel_IsHidden_AndAnotherLabelIsShown()
    {
        var response = Response(Provider("claude", Account("work"), Account("home")));

        var selected = RemoteAccountSelection.Select(response, LocalWork);

        var only = Assert.Single(selected);
        Assert.Equal("claude", only.ProviderId);
        Assert.Equal("home", only.AccountLabel);
    }

    [Fact]
    public void AnUnlabelledAccount_IsNeverShown()
    {
        // It could be this machine's own pre-label reading.
        var response = Response(Provider("claude", Account(null), Account("")));

        Assert.Empty(RemoteAccountSelection.Select(response, LocalWork));
    }

    [Fact]
    public void WithNoLocalLabel_NothingIsShownForThatProvider()
    {
        // Without a label of its own this machine cannot tell which account is its own.
        var response = Response(Provider("claude", Account("home"), Account("other")));

        Assert.Empty(RemoteAccountSelection.Select(response, AccountLabels.None));
    }

    [Fact]
    public void AProviderOutsideTheAllowList_IsHidden()
    {
        var response = Response(
            Provider("codex", Account("home")),
            Provider("google", Account("home")),
            Provider("zai", Account("home")));

        Assert.Empty(RemoteAccountSelection.Select(response, LocalWork));
    }

    [Fact]
    public void TheAllowList_IsClaudeOnly()
    {
        Assert.Equal(new[] { "claude" }, RemoteAccountSelection.SupportedProviders.Order().ToArray());
    }

    [Fact]
    public void ThePerProviderOverride_IsTheLabelComparedAgainst()
    {
        // Default "work", but Claude published as "home": the "home" account is ours and
        // the "work" one is someone else's.
        var labels = AccountLabels.Create("work", [new("claude", "home")]);
        var response = Response(Provider("claude", Account("work"), Account("home")));

        var only = Assert.Single(RemoteAccountSelection.Select(response, labels));
        Assert.Equal("work", only.AccountLabel);
    }

    [Fact]
    public void ProviderIds_AreMatchedWithoutRegardToCaseOrPadding()
    {
        var response = Response(Provider(" Claude ", Account("home")));

        var only = Assert.Single(RemoteAccountSelection.Select(response, LocalWork));
        Assert.Equal("claude", only.ProviderId);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("not-configured")]
    public void AnAccountTheOtherMachineHasNoReadingFor_IsHidden(string state)
    {
        var response = Response(Provider("claude", Account("home", state: state)));

        Assert.Empty(RemoteAccountSelection.Select(response, LocalWork));
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("has space")]
    [InlineData("1starts-with-digit")]
    public void ALabelTheApiWouldRefuse_IsHidden(string label)
    {
        var response = Response(Provider("claude", Account(label)));

        Assert.Empty(RemoteAccountSelection.Select(response, LocalWork));
    }

    [Fact]
    public void ADuplicateLabel_IsShownOnce()
    {
        var response = Response(Provider("claude", Account("home", plan: "First"), Account("home", plan: "Second")));

        var only = Assert.Single(RemoteAccountSelection.Select(response, LocalWork));
        Assert.Equal("First", only.PlanLabel);
    }

    [Fact]
    public void ObservedAt_FallsBackToReceivedAt()
    {
        var received = Observed.AddSeconds(5);
        var response = Response(Provider("claude",
            new RemoteAccountReading("2", "home", "available", null, [], null, received)));

        Assert.Equal(received, Assert.Single(RemoteAccountSelection.Select(response, LocalWork)).ObservedAt);
    }

    [Fact]
    public void GaugesAndPlan_AreCarriedThrough_NullGaugesDropped()
    {
        var gauge = new RemoteGauge("claude-5h", "5h", "Claude Code", 73m, Observed.AddHours(1), 18000);
        var response = Response(Provider("claude",
            new RemoteAccountReading("2", "home", "available", "Plan B", [gauge, null!], Observed, Observed)));

        var only = Assert.Single(RemoteAccountSelection.Select(response, LocalWork));
        Assert.Equal("Plan B", only.PlanLabel);
        Assert.Equal("available", only.State);
        Assert.Equal(Observed, only.ObservedAt);
        Assert.Equal(gauge, Assert.Single(only.Gauges));
    }

    [Fact]
    public void AnEmptyOrNullResponse_SelectsNothing()
    {
        Assert.Empty(RemoteAccountSelection.Select(null, LocalWork));
        Assert.Empty(RemoteAccountSelection.Select(new RemoteAccountsResponse(2, null, null, null), LocalWork));
        Assert.Empty(RemoteAccountSelection.Select(Response(new RemoteProviderAccounts("claude", null)), LocalWork));
    }

    // ------------------------------------------------------------ freshness

    [Theory]
    [InlineData(0, RemoteAccountAge.Fresh)]
    [InlineData(19, RemoteAccountAge.Fresh)]
    [InlineData(20, RemoteAccountAge.Fresh)]
    [InlineData(21, RemoteAccountAge.Stale)]
    [InlineData(23 * 60, RemoteAccountAge.Stale)]
    [InlineData(24 * 60, RemoteAccountAge.Stale)]
    [InlineData(24 * 60 + 1, RemoteAccountAge.Expired)]
    public void Freshness_TurnsStalePast20Minutes_AndExpiresPast24Hours(int minutesOld, RemoteAccountAge expected)
    {
        Assert.Equal(expected, RemoteAccountFreshness.Classify(Observed, Observed.AddMinutes(minutesOld)));
    }

    [Fact]
    public void Freshness_ReadingStampedInTheFuture_IsFresh()
    {
        // Clock skew between two machines must not hide a perfectly good reading.
        Assert.Equal(RemoteAccountAge.Fresh, RemoteAccountFreshness.Classify(Observed.AddMinutes(3), Observed));
    }

    [Fact]
    public void Freshness_AReadingWithNoTime_IsExpired()
    {
        Assert.Equal(RemoteAccountAge.Expired, RemoteAccountFreshness.Classify(null, Observed));
    }

    private static RemoteAccountsResponse Response(params RemoteProviderAccounts[] providers) =>
        new(2, Observed, Observed, providers);

    private static RemoteProviderAccounts Provider(string id, params RemoteAccountReading[] accounts) =>
        new(id, accounts);

    private static RemoteAccountReading Account(string? label, string state = "available", string? plan = null) =>
        new("synthetic-" + label, label, state, plan,
            [new RemoteGauge("claude-5h", "5h", null, 50m, null, null)],
            Observed, Observed);
}
