using System.Text;
using VibeMeter.Providers.Codex;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// CodexAuth.ReadExpiry is a pure claims read off a JWT's payload: it must
/// recover the expiry from a well-formed token, tolerate base64url payloads
/// that need padding (length not a multiple of four — the real Codex CLI
/// issues exactly such tokens) and the base64url alphabet, and answer null —
/// never throw — for anything malformed. Every token here is constructed; the
/// real ~/.codex/auth.json is never read and no real token is embedded.
/// </summary>
public sealed class CodexAuthTests
{
    private const long ExpirySeconds = 1_800_000_000;

    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(ExpirySeconds);

    [Fact]
    public void WellFormedToken_WithAnExpClaim_YieldsTheExpiry()
    {
        var token = Token("""{"sub":"acct-1","iat":1700000000,"exp":1800000000}""");

        var expiry = CodexAuth.ReadExpiry(token);

        Assert.Equal(Expiry, expiry);
        Assert.Equal(TimeSpan.Zero, expiry!.Value.Offset);
    }

    [Fact]
    public void UnsignedTwoSegmentToken_StillYieldsTheExpiry()
    {
        // The expiry is read from the payload only, and the method makes no
        // trust decision: a header.payload pair with no signature still has a
        // readable exp claim.
        var payload = Base64Url(Encoding.UTF8.GetBytes($$"""{"exp":{{ExpirySeconds}}}"""));

        Assert.Equal(Expiry, CodexAuth.ReadExpiry($"header.{payload}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PayloadsOfConsecutiveLengths_PaddedOrNot_YieldTheExpiry(int fillerLength)
    {
        // Stepping the payload through eight consecutive byte lengths walks the
        // base64url text through every length residue it can have (0, 2 and 3
        // mod 4 — 1 is impossible), so padded and unpadded decodings are both
        // exercised. This padding case is the likeliest to regress.
        var payloadJson = $"{{\"exp\":{ExpirySeconds},\"filler\":\"{new string('x', fillerLength)}\"}}";

        Assert.Equal(Expiry, CodexAuth.ReadExpiry(Token(payloadJson)));
    }

    [Fact]
    public void Base64UrlAlphabet_DashesAndUnderscores_Parses()
    {
        // The filler bytes ("z" + two ¿ + one ࠀ) land 6-bit groups on both
        // '+' (encoded as '-') and '/' (encoded as '_'), so the payload really
        // uses the URL-safe alphabet.
        var filler = "z" + "\u00bf\u00bf" + "\u0800";
        var payloadJson = $"{{\"exp\":{ExpirySeconds},\"p\":\"{filler}\"}}";
        var encoded = Base64Url(Encoding.UTF8.GetBytes(payloadJson));

        // Precondition: the crafted payload genuinely contains both
        // characters, otherwise this test would pass without exercising the
        // case it exists for.
        Assert.Contains('-', encoded);
        Assert.Contains('_', encoded);

        Assert.Equal(Expiry, CodexAuth.ReadExpiry(Token(payloadJson)));
    }

    [Fact]
    public void MissingExpClaim_YieldsNull()
    {
        var token = Token("""{"sub":"acct-1","iat":1700000000}""");

        Assert.Null(CodexAuth.ReadExpiry(token));
    }

    [Theory]
    [InlineData("""{"exp":"1800000000"}""")] // quoted: a string, not a number
    [InlineData("""{"exp":1.5}""")]          // fractional: not representable as seconds
    [InlineData("""{"exp":null}""")]
    [InlineData("""{"exp":true}""")]
    [InlineData("""{"exp":99999999999999999999}""")] // overflows Int64
    public void NonNumericExpClaim_YieldsNull(string payloadJson)
    {
        Assert.Null(CodexAuth.ReadExpiry(Token(payloadJson)));
    }

    [Theory]
    [InlineData(null)] // no token at all
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")] // no dots
    [InlineData("only-one-dot.")]
    [InlineData("empty..payload")] // empty payload segment
    [InlineData("a.b")] // "b" is an invalid base64 length
    [InlineData("$$$.$$$")] // not base64 either
    public void MalformedTokens_YieldNull_NeverThrow(string? accessToken)
    {
        Assert.Null(CodexAuth.ReadExpiry(accessToken));
    }

    [Theory]
    [InlineData("certainly not json")] // valid base64, decodes to text
    [InlineData("\u00ff\x00\xde\xad")] // valid base64, decodes to binary
    public void ValidBase64ThatIsNotJson_YieldsNull(string payloadText)
    {
        var payload = Base64Url(Encoding.UTF8.GetBytes(payloadText));

        Assert.Null(CodexAuth.ReadExpiry($"header.{payload}.sig"));
    }

    /// <summary>Builds header.payload.signature; only the payload is decoded.</summary>
    private static string Token(string payloadJson) =>
        $"{Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))}"
        + $".{Base64Url(Encoding.UTF8.GetBytes(payloadJson))}.sig";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
