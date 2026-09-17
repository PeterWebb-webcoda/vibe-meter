using VibeMeter.Agent;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Pins the agent's command-line contract: no arguments still selects the
/// daemon; --once, --dry-run and --help select their modes; --once --dry-run
/// behaves as a dry-run; and unknown arguments are a hard usage error that
/// never falls through to the daemon loop.
/// </summary>
public sealed class AgentCliTests
{
    [Fact]
    public void NoArguments_SelectsTheDaemon()
    {
        Assert.Equal(AgentLaunchMode.Daemon, CliOptions.Parse([]).Mode);
    }

    [Fact]
    public void Once_SelectsTheOneShotMode()
    {
        Assert.Equal(AgentLaunchMode.Once, CliOptions.Parse(["--once"]).Mode);
    }

    [Fact]
    public void DryRun_SelectsTheDryRunMode()
    {
        Assert.Equal(AgentLaunchMode.DryRun, CliOptions.Parse(["--dry-run"]).Mode);
    }

    [Fact]
    public void OnceCombinedWithDryRun_BehavesAsADryRun()
    {
        Assert.Equal(AgentLaunchMode.DryRun, CliOptions.Parse(["--once", "--dry-run"]).Mode);
    }

    [Fact]
    public void Help_SelectsHelpAndWinsOverOtherFlags()
    {
        Assert.Equal(AgentLaunchMode.Help, CliOptions.Parse(["--once", "--help"]).Mode);
        Assert.Equal(AgentLaunchMode.Help, CliOptions.Parse(["--dry-run", "-h"]).Mode);
    }

    [Fact]
    public void UnknownArgument_IsAUsageErrorNamingTheArgument()
    {
        var options = CliOptions.Parse(["--bogus"]);

        Assert.Equal(AgentLaunchMode.UsageError, options.Mode);
        Assert.Contains("--bogus", options.UsageError);
    }

    [Fact]
    public void UnknownArgumentAlongsideValidFlags_IsStillAUsageError()
    {
        // A typo must never be silently forgiven into some other mode.
        Assert.Equal(AgentLaunchMode.UsageError, CliOptions.Parse(["--dry-run", "--bogus"]).Mode);
        Assert.Equal(AgentLaunchMode.UsageError, CliOptions.Parse(["once"]).Mode);
        Assert.Equal(AgentLaunchMode.UsageError, CliOptions.Parse(["--dry-run", "extra"]).Mode);
    }

    [Fact]
    public async Task UnknownArgument_ExitsNonZero_WithoutStartingTheLoop()
    {
        // Main returns a usage error before any configuration is read or any
        // host is built - if this ever started the daemon loop it would hang
        // instead of returning.
        Assert.Equal(2, await Program.Main(["--bogus"]));
    }

    [Fact]
    public async Task Help_ExitsZero()
    {
        Assert.Equal(0, await Program.Main(["--help"]));
    }

    [Fact]
    public void OnceExitCode_ZeroOnlyForPublishedOrNothingToPublish()
    {
        Assert.Equal(0, Program.OnceExitCode(CycleOutcome.Published));
        Assert.Equal(0, Program.OnceExitCode(CycleOutcome.NothingToPublish));
        Assert.Equal(1, Program.OnceExitCode(CycleOutcome.QueuedForLater));
        Assert.Equal(1, Program.OnceExitCode(CycleOutcome.RejectedPermanently));
        Assert.Equal(1, Program.OnceExitCode(CycleOutcome.CollectionFailed));
    }
}
