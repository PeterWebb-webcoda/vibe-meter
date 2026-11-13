using System;
using System.Linq;
using System.Threading.Tasks;
using VibeMeter.Core;
using VibeMeter.Publishing;
using VibeMeter.Ui.ViewModels;
using Xunit;

namespace VibeMeter.Tests.Ui;

/// <summary>
/// The read-only cards for other machines' accounts: how they are titled, aged, dimmed
/// and dropped, and that drawing them leaves the local card's own figures alone.
/// </summary>
public sealed class RemoteAccountCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshReading_IsTitledByLabel_AndSaysWhereItCameFrom()
    {
        var card = RemoteAccountViewModel.From(Remote("home", Now.AddMinutes(-4)), "Claude Code", Now);

        Assert.NotNull(card);
        Assert.Equal("Claude Code · Home", card.Title);
        Assert.Equal("From another machine · observed 4 min ago", card.Note);
        Assert.False(card.IsStale);
        Assert.Equal(1.0, card.CardOpacity);
        Assert.Equal("Plan B", card.PlanLabel);
        Assert.True(card.HasPlanLabel);
        Assert.Equal(ProviderAccent.For("claude"), card.Accent);
    }

    [Fact]
    public void AReadingOlderThan20Minutes_IsDimmedAndMarkedLastSeen()
    {
        var card = RemoteAccountViewModel.From(Remote("home", Now.AddHours(-2)), "Claude Code", Now);

        Assert.NotNull(card);
        Assert.True(card.IsStale);
        Assert.Equal(RemoteAccountViewModel.StaleOpacity, card.CardOpacity);
        Assert.Equal("From another machine · last seen 2 h ago", card.Note);
    }

    [Fact]
    public void AReadingOlderThan24Hours_IsNotShownAtAll()
    {
        Assert.Null(RemoteAccountViewModel.From(Remote("home", Now.AddHours(-25)), "Claude Code", Now));
        Assert.Null(RemoteAccountViewModel.From(Remote("home", null), "Claude Code", Now));
    }

    [Fact]
    public void Gauges_KeepTheRemainingSense_AndAGaugeWithNoFigureIsLeftOut()
    {
        var reset = new DateTimeOffset(2026, 11, 13, 14, 59, 59, TimeSpan.Zero);
        var account = Remote("home", Now, gauges:
        [
            new RemoteGauge("claude-5h", "5h", "Claude Code", 72.5m, reset, 18000),
            new RemoteGauge("claude-week", null, null, 140m, null, null),
            new RemoteGauge("claude-opus", "Opus", null, null, null, null),
        ]);

        var card = RemoteAccountViewModel.From(account, "Claude Code", Now)!;

        Assert.Equal(2, card.Gauges.Count);

        var fiveHour = card.Gauges[0];
        Assert.Equal("5h", fiveHour.Title);
        Assert.Equal("Claude Code", fiveHour.Subtitle);
        Assert.Equal(73, fiveHour.Percent); // remaining, rounded - not inverted
        Assert.Equal(reset.LocalDateTime, fiveHour.ResetAt);

        var week = card.Gauges[1];
        Assert.Equal("claude-week", week.Title); // falls back to the id
        Assert.Equal(100, week.Percent);         // clamped
        Assert.Null(week.ResetAt);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(60 * 60, "1 h ago")]
    [InlineData(23 * 60 * 60 + 59 * 60, "23 h ago")]
    [InlineData(-120, "just now")]
    public void Ages_AreWordedShortly(int seconds, string expected)
    {
        Assert.Equal(expected, RemoteAccountViewModel.FormatAgo(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void AProviderCard_ShowsOnlyItsOwnProvidersRemoteAccounts()
    {
        var claude = new ProviderViewModel(new StubProvider("claude", "Claude Code"));

        claude.ShowRemoteAccounts(
        [
            Remote("home", Now),
            Remote("other", Now.AddHours(-30)),          // too old: dropped
            Remote("home", Now) with { ProviderId = "codex" },
        ], Now);

        var only = Assert.Single(claude.RemoteAccounts);
        Assert.Equal("Claude Code · Home", only.Title);
        Assert.True(claude.HasRemoteAccounts);

        claude.ShowRemoteAccounts([], Now);
        Assert.Empty(claude.RemoteAccounts);
        Assert.False(claude.HasRemoteAccounts);
    }

    [Fact]
    public void DrawingRemoteAccounts_LeavesTheLocalCardsFiguresAlone()
    {
        // The compact bar, the tray summary and the cost panel all read these.
        var claude = new ProviderViewModel(new StubProvider("claude", "Claude Code"));
        claude.Apply(new ProviderUsage
        {
            ProviderId = "claude",
            DisplayName = "Claude Code",
            State = ProviderState.Ok,
            PlanLabel = "Local Plan",
            Gauges = [new UsageGauge("claude-5h", "5h", null, 64, null), new UsageGauge("claude-week", "Week", null, 81, null)],
        });

        claude.ShowRemoteAccounts([Remote("home", Now)], Now);

        Assert.Equal(new[] { 64, 81 }, claude.Gauges.Select(g => g.Percent).ToArray());
        Assert.Equal(64, claude.PrimaryPercent);
        Assert.Equal(64, claude.FiveHourPercent);
        Assert.Equal(81, claude.WeeklyPercent);
        Assert.Equal("Local Plan", claude.PlanLabel);
    }

    private static RemoteAccount Remote(string label, DateTimeOffset? observedAt, RemoteGauge[]? gauges = null) =>
        new("claude", label, "available", "Plan B",
            gauges ?? [new RemoteGauge("claude-5h", "5h", null, 50m, null, null)],
            observedAt);

    private sealed class StubProvider(string id, string displayName) : IUsageProvider
    {
        public string Id => id;

        public string DisplayName => displayName;

        public Task<ProviderUsage> FetchAsync() => throw new NotSupportedException("never fetched in these tests");
    }
}
