using VibeMeter.Agent;
using VibeMeter.Agent.AccessToken;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Covers how the agent is told to authenticate: the --login mode, the
/// device-code configuration contract, and which token provider is selected.
/// </summary>
/// <remarks>
/// None of this performs a sign-in. It pins the decisions made BEFORE any
/// network call — which are the ones that decide whether an unattended machine
/// starts at all, and which fail in the most confusing ways when wrong.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class DeviceCodeAuthTests
{
    private static readonly string[] AuthVariables =
    [
        DeviceCodeAuthOptions.ClientIdVariable,
        DeviceCodeAuthOptions.TenantIdVariable,
        DeviceCodeAuthOptions.ScopeVariable,
        AgentConfig.TokenVariable,
    ];

    [Fact]
    public void Login_IsItsOwnMode()
    {
        Assert.Equal(AgentLaunchMode.Login, CliOptions.Parse(["--login"]).Mode);
    }

    [Fact]
    public void Help_WinsOverLogin()
    {
        Assert.Equal(AgentLaunchMode.Help, CliOptions.Parse(["--login", "--help"]).Mode);
    }

    [Fact]
    public void Login_WinsOverOnceAndDryRun()
    {
        // Signing in is a deliberate one-off act; it must not be reinterpreted
        // as a publish cycle just because another flag was also supplied.
        Assert.Equal(AgentLaunchMode.Login, CliOptions.Parse(["--once", "--login"]).Mode);
        Assert.Equal(AgentLaunchMode.Login, CliOptions.Parse(["--dry-run", "--login"]).Mode);
    }

    [Fact]
    public void UnknownArgument_BeatsLogin_AndNeverStartsAnything()
    {
        Assert.Equal(AgentLaunchMode.UsageError, CliOptions.Parse(["--login", "--typo"]).Mode);
    }

    [Fact]
    public void HelpText_DocumentsLoginAndItsVariables()
    {
        Assert.Contains("--login", CliOptions.HelpText);
        Assert.Contains(DeviceCodeAuthOptions.ClientIdVariable, CliOptions.HelpText);
        Assert.Contains(DeviceCodeAuthOptions.TenantIdVariable, CliOptions.HelpText);
        Assert.Contains(DeviceCodeAuthOptions.ScopeVariable, CliOptions.HelpText);
    }

    [Fact]
    public void Options_WithEverythingSet_AreReadAndTrimmed()
    {
        WithVariables(
            new()
            {
                [DeviceCodeAuthOptions.ClientIdVariable] = "  client-id  ",
                [DeviceCodeAuthOptions.TenantIdVariable] = "tenant-id",
                [DeviceCodeAuthOptions.ScopeVariable] = "api://resource/Usage.Write",
            },
            () =>
            {
                var options = DeviceCodeAuthOptions.FromEnvironment();

                Assert.Equal("client-id", options.ClientId);
                Assert.Equal("tenant-id", options.TenantId);
                Assert.Equal("https://login.microsoftonline.com/tenant-id", options.Authority);
                Assert.Equal(["api://resource/Usage.Write"], options.Scopes);
            });
    }

    [Fact]
    public void Options_WithNothingSet_ReportEveryMissingVariableAtOnce()
    {
        // An operator on a headless box should learn about all three problems in
        // one run, not discover them one restart at a time.
        WithVariables([], () =>
        {
            var exception = Assert.Throws<AgentConfigException>(DeviceCodeAuthOptions.FromEnvironment);

            Assert.Contains(DeviceCodeAuthOptions.ClientIdVariable, exception.Message);
            Assert.Contains(DeviceCodeAuthOptions.TenantIdVariable, exception.Message);
            Assert.Contains(DeviceCodeAuthOptions.ScopeVariable, exception.Message);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_TreatBlankAsMissing(string blank)
    {
        WithVariables(
            new()
            {
                [DeviceCodeAuthOptions.ClientIdVariable] = "client-id",
                [DeviceCodeAuthOptions.TenantIdVariable] = blank,
                [DeviceCodeAuthOptions.ScopeVariable] = "api://resource/Usage.Write",
            },
            () =>
            {
                var exception = Assert.Throws<AgentConfigException>(DeviceCodeAuthOptions.FromEnvironment);
                Assert.Contains(DeviceCodeAuthOptions.TenantIdVariable, exception.Message);
            });
    }

    [Fact]
    public void WithoutClientId_TheEnvironmentTokenProviderIsUsed()
    {
        WithVariables(new() { [AgentConfig.TokenVariable] = "a-token" }, () =>
            Assert.IsType<EnvironmentAccessTokenProvider>(Program.CreateAccessTokenProvider()));
    }

    [Fact]
    public void WithClientId_TheDeviceCodeProviderIsUsed()
    {
        WithVariables(
            new()
            {
                [DeviceCodeAuthOptions.ClientIdVariable] = "client-id",
                [DeviceCodeAuthOptions.TenantIdVariable] = "tenant-id",
                [DeviceCodeAuthOptions.ScopeVariable] = "api://resource/Usage.Write",
            },
            () => Assert.IsType<DeviceCodeAccessTokenProvider>(Program.CreateAccessTokenProvider()));
    }

    [Fact]
    public void PartialDeviceCodeConfiguration_FailsLoudly_RatherThanFallingBackToATokenProvider()
    {
        // Silently falling back would turn a half-finished setup into a confusing
        // "token is required" error pointing at the wrong variable entirely.
        WithVariables(
            new()
            {
                [DeviceCodeAuthOptions.ClientIdVariable] = "client-id",
                [AgentConfig.TokenVariable] = "a-token",
            },
            () =>
            {
                var exception = Assert.Throws<AgentConfigException>(Program.CreateAccessTokenProvider);
                Assert.Contains(DeviceCodeAuthOptions.TenantIdVariable, exception.Message);
            });
    }

    private static void WithVariables(Dictionary<string, string?> values, Action body)
    {
        var saved = AuthVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in AuthVariables)
            {
                Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));
            }

            body();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static void WithVariables(Action body) => WithVariables([], body);
}
