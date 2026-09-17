using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Groups every test class that mutates environment variables.
/// </summary>
/// <remarks>
/// Environment variables are process-global, and xUnit runs test classes in
/// parallel by default, so two such classes can otherwise clobber one another
/// and fail intermittently for reasons unrelated to the code under test.
/// Sharing one collection makes them run sequentially. Any new test class that
/// sets an environment variable belongs here too.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "environment-variables";
}
