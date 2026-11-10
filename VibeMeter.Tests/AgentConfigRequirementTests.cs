using VibeMeter.Agent;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Pins the configuration contract behind the CLI modes: the daemon and
/// --once need an API base URL and a token, while --dry-run runs with NEITHER
/// set — on that path the token is genuinely absent, never a dummy value.
/// Values that ARE set are still validated in every mode. Environment
/// variables are process-global, so every test saves and restores them.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class AgentConfigRequirementTests
{
    private static readonly string[] AllVariables =
    [
        AgentConfig.ApiBaseUrlVariable,
        AgentConfig.IntervalSecondsVariable,
        AgentConfig.TokenVariable,
        AgentConfig.StalenessMinutesVariable,
        AgentConfig.AccountLabelVariable,
        AgentConfig.AccountLabelOverridesVariable,
    ];

    [Fact]
    public void DefaultRequirements_WithNothingSet_RequireBaseUrlAndToken()
    {
        WithVariables([], () =>
        {
            var exception = Assert.Throws<AgentConfigException>(AgentConfig.FromEnvironment);

            Assert.Contains(AgentConfig.ApiBaseUrlVariable, exception.Message);
            Assert.Contains(AgentConfig.TokenVariable, exception.Message);
        });
    }

    [Fact]
    public void DryRunRequirements_WithNothingSet_SucceedWithDefaults()
    {
        WithVariables([], () =>
        {
            var config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);

            Assert.Null(config.ApiBaseUrl);
            Assert.Equal(AgentConfig.DefaultInterval, config.Interval);
            Assert.Equal(AgentConfig.DefaultStalenessThreshold, config.StalenessThreshold);
            Assert.NotEmpty(config.QueueDirectory);
        });
    }

    [Fact]
    public void DryRunRequirements_StillHonourSetValues()
    {
        WithVariables(
            new Dictionary<string, string?>
            {
                [AgentConfig.ApiBaseUrlVariable] = "https://api.example.com/",
                [AgentConfig.IntervalSecondsVariable] = "30",
                [AgentConfig.StalenessMinutesVariable] = "45",
            },
            () =>
            {
                var config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);

                Assert.Equal(new Uri("https://api.example.com"), config.ApiBaseUrl);
                Assert.Equal(TimeSpan.FromSeconds(30), config.Interval);
                Assert.Equal(TimeSpan.FromMinutes(45), config.StalenessThreshold);
            });
    }

    [Fact]
    public void DryRunRequirements_StillRejectInvalidValues()
    {
        WithVariables(
            new Dictionary<string, string?> { [AgentConfig.IntervalSecondsVariable] = "whenever" },
            () => Assert.Throws<AgentConfigException>(
                () => AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false)));
    }

    [Fact]
    public void AccountLabels_AreReadFromTheEnvironment_AndAnInvalidOneIsIgnoredNotFatal()
    {
        WithVariables(
            new Dictionary<string, string?>
            {
                [AgentConfig.AccountLabelVariable] = "personal",
                [AgentConfig.AccountLabelOverridesVariable] = "codex=shared, google=Not Valid",
            },
            () =>
            {
                var config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);

                Assert.Equal("personal", config.AccountLabels.For("claude"));
                Assert.Equal("shared", config.AccountLabels.For("codex"));
                Assert.Equal("personal", config.AccountLabels.For("google"));
                Assert.Single(config.AccountLabels.Warnings);
            });
    }

    [Fact]
    public void AccountLabels_Unset_AreNone()
    {
        WithVariables([], () =>
        {
            var config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);

            Assert.Null(config.AccountLabels.For("claude"));
            Assert.Empty(config.AccountLabels.Warnings);
        });
    }

    /// <summary>Clears every VIBEMETER_* variable, applies the given overrides,
    /// runs the action, then restores the original environment.</summary>
    private static void WithVariables(Dictionary<string, string?> variables, Action action)
    {
        var saved = AllVariables.ToDictionary(
            variable => variable,
            variable => Environment.GetEnvironmentVariable(variable));
        try
        {
            foreach (var variable in AllVariables)
            {
                Environment.SetEnvironmentVariable(variable, variables.GetValueOrDefault(variable));
            }

            action();
        }
        finally
        {
            foreach (var (variable, value) in saved)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }
    }
}
