using AgentDock.Services.Remote;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for the pairing code and its proof exchange.
///
/// The code is read off one screen and typed on another, so the interesting cases are the human
/// ones: separators, casing, and the glyphs people substitute. The security-relevant case is that
/// a wrong code produces a failing proof — the code itself never crosses the wire, so if
/// verification were loose there would be nothing else protecting the port.
/// </summary>
public class PairingCodeTests
{
    [Fact]
    public void GeneratedCodeAvoidsAmbiguousGlyphs()
    {
        // 0/O, 1/I/L and U are excluded precisely because they are misread when transcribed.
        const string forbidden = "01OILU";

        for (var i = 0; i < 200; i++)
        {
            var code = PairingCode.Generate();
            Assert.Equal(PairingCode.Length, code.Length);
            Assert.DoesNotContain(code, c => forbidden.Contains(c));
        }
    }

    [Fact]
    public void FormatSplitsIntoTwoGroups()
        => Assert.Equal("ABCD-EFGH", PairingCode.Format("ABCDEFGH"));

    [Theory]
    [InlineData("ABCD-EFGH", "ABCDEFGH")]
    [InlineData("abcd efgh", "ABCDEFGH")]
    [InlineData("  ABCDEFGH  ", "ABCDEFGH")]
    [InlineData("ABCD_EFGH", "ABCDEFGH")]
    public void NormalizeStripsSeparatorsAndUpperCases(string typed, string expected)
        => Assert.Equal(expected, PairingCode.Normalize(typed));

    [Fact]
    public void NormalizeMapsSubstitutedGlyphs()
    {
        // A user typing the letter O meant the digit 0 — except 0 is not in the alphabet either,
        // so this can only ever produce a code that fails. What matters is that it fails as a
        // wrong code rather than being silently rewritten into a different *valid* one.
        Assert.Equal("V", PairingCode.Normalize("U"));
        Assert.Equal("1", PairingCode.Normalize("I"));
        Assert.Equal("1", PairingCode.Normalize("L"));
        Assert.Equal("0", PairingCode.Normalize("O"));
    }

    [Fact]
    public void NormalizeOfNothingIsEmpty()
    {
        Assert.Equal("", PairingCode.Normalize(null));
        Assert.Equal("", PairingCode.Normalize("   "));
    }

    [Fact]
    public void CorrectCodeProducesAVerifiableProof()
    {
        var code = PairingCode.Generate();
        var nonce = PairingCode.NewNonce();

        var proof = PairingCode.ComputeProof(code, nonce);

        Assert.True(PairingCode.VerifyProof(code, nonce, proof));
    }

    [Fact]
    public void WrongCodeFailsVerification()
    {
        var nonce = PairingCode.NewNonce();
        var proof = PairingCode.ComputeProof("ABCDEFGH", nonce);

        Assert.False(PairingCode.VerifyProof("ABCDEFGJ", nonce, proof));
    }

    [Fact]
    public void ProofIsBoundToTheNonce()
    {
        // Replaying a proof against a fresh challenge must fail, or a captured proof would be
        // reusable forever.
        var code = PairingCode.Generate();
        var proof = PairingCode.ComputeProof(code, PairingCode.NewNonce());

        Assert.False(PairingCode.VerifyProof(code, PairingCode.NewNonce(), proof));
    }

    [Fact]
    public void ProofIsCaseInsensitiveInTheCode()
    {
        // The entry box upper-cases, but the server holds whatever it generated; the two must agree.
        var nonce = PairingCode.NewNonce();
        Assert.Equal(
            PairingCode.ComputeProof("ABCDEFGH", nonce),
            PairingCode.ComputeProof("abcdefgh", nonce));
    }

    [Fact]
    public void MissingOrEmptyProofIsRejected()
    {
        var nonce = PairingCode.NewNonce();
        Assert.False(PairingCode.VerifyProof("ABCDEFGH", nonce, null));
        Assert.False(PairingCode.VerifyProof("ABCDEFGH", nonce, ""));
    }

    [Fact]
    public void SessionTokensAreDistinctAndCompareCorrectly()
    {
        var a = PairingCode.NewSessionToken();
        var b = PairingCode.NewSessionToken();

        Assert.NotEqual(a, b);
        Assert.True(PairingCode.TokensMatch(a, a));
        Assert.False(PairingCode.TokensMatch(a, b));

        // A null stored token must never match an absent offered one, or a client that sent no
        // token would be admitted before it had ever paired.
        Assert.False(PairingCode.TokensMatch(null, null));
        Assert.False(PairingCode.TokensMatch(a, null));
        Assert.False(PairingCode.TokensMatch(null, a));
    }
}
