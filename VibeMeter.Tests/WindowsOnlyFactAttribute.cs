using System;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips on non-Windows platforms instead of failing.
/// </summary>
/// <remarks>
/// DPAPI has no counterpart outside Windows, and
/// <c>VibeMeter.Core.Security.DpapiSecretProtector</c> short-circuits to <c>false</c>
/// there rather than throwing. A test that asserts a successful round trip therefore
/// cannot pass on Linux or macOS by design. Skipping states that, where a filter passed
/// on the command line only hides it — and a suite that cannot go green on the platform
/// it is developed on stops being run at all.
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows: DPAPI is unavailable on this platform, " +
                   "so DpapiSecretProtector cannot protect or unprotect a value.";
        }
    }
}
