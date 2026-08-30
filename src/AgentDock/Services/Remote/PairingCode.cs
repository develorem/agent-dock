using System.Security.Cryptography;
using System.Text;

namespace AgentDock.Services.Remote;

/// <summary>
/// The eight-character pairing code and the proof exchange built on it.
///
/// The code is read off one screen and typed on another, so the alphabet excludes every
/// glyph pair a human confuses: no <c>0</c>/<c>O</c>, no <c>1</c>/<c>I</c>/<c>L</c>, no
/// <c>U</c> (mistyped as <c>V</c>). That leaves 30 symbols, giving 30^8 (about 6.5x10^11)
/// combinations — ample, but only because attempts are rate-limited server-side. Brute force
/// is defeated by the lockout, not by the entropy alone.
///
/// The code itself never crosses the wire. The server sends a nonce; the client returns an
/// HMAC over it keyed by the code. Even inside TLS this avoids handing the secret to a peer
/// that turns out not to be who it claimed.
/// </summary>
public static class PairingCode
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 8;

    /// <summary>Generates a fresh code. Called every time server mode starts.</summary>
    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Formats a code for display as two groups of four.</summary>
    public static string Format(string code)
        => code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>
    /// Accepts what a user typed: strips separators and whitespace, upper-cases, and maps the
    /// characters they were most likely to substitute back onto the real alphabet. Being
    /// forgiving here costs nothing and saves a support conversation about a lowercase L.
    /// </summary>
    public static string Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return "";

        var sb = new StringBuilder(Length);
        foreach (var raw in typed.Trim().ToUpperInvariant())
        {
            var c = raw switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                'U' => 'V',
                _ => raw,
            };

            // '0' and '1' are not in the alphabet at all; a user who typed them meant O/I,
            // which are themselves excluded, so there is nothing sensible to map onto. Drop
            // only true separators and keep everything else so a wrong code fails as a wrong
            // code rather than being silently rewritten into a different valid-looking one.
            if (c is ' ' or '-' or '_') continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    public static string NewNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Proof the client sends: HMAC-SHA256 over the server nonce, keyed by the code.</summary>
    public static string ComputeProof(string code, string nonce)
    {
        var key = Encoding.UTF8.GetBytes(code.ToUpperInvariant());
        var data = Encoding.UTF8.GetBytes(nonce);
        return Convert.ToBase64String(HMACSHA256.HashData(key, data));
    }

    /// <summary>
    /// Constant-time comparison of the expected and received proofs. A length-independent
    /// compare would leak how much of a guess was right.
    /// </summary>
    public static bool VerifyProof(string code, string nonce, string? offeredProof)
    {
        if (string.IsNullOrEmpty(offeredProof)) return false;

        var expected = Encoding.UTF8.GetBytes(ComputeProof(code, nonce));
        var offered = Encoding.UTF8.GetBytes(offeredProof);
        return CryptographicOperations.FixedTimeEquals(expected, offered);
    }

    /// <summary>A long-lived token so a network blip does not re-prompt the user for the code.</summary>
    public static string NewSessionToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool TokensMatch(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
