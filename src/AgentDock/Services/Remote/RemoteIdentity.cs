using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// The server's TLS identity, and the client's record of servers it has already trusted.
///
/// The pairing code proves <i>authorization</i> (this person is allowed in). The certificate
/// proves <i>identity</i> (this is the machine I paired with last time). Both are needed: a code
/// alone is replayable by whatever sits in the middle on a hostile network.
///
/// The certificate is self-signed and pinned on first pairing — trust on first use, as SSH does.
/// A changed fingerprint on a later connect is surfaced as a warning rather than silently
/// accepted.
/// </summary>
public sealed class RemoteIdentity(ILogService log, IAppSettingsStore appSettings)
{
    private const string CertFileName = "remote-server.pfx";
    private const string KnownHostsFileName = "remote-known-hosts.txt";

    private string RemoteDir => Path.Combine(appSettings.SettingsDir, "remote");
    private string CertPath => Path.Combine(RemoteDir, CertFileName);
    private string KnownHostsPath => Path.Combine(RemoteDir, KnownHostsFileName);

    /// <summary>
    /// Loads the persisted server certificate, generating one on first use. Persisting it means
    /// a client's pinned fingerprint survives a server restart; regenerating every launch would
    /// make the pin useless and train the user to click through the warning.
    /// </summary>
    public X509Certificate2 LoadOrCreateServerCertificate()
    {
        Directory.CreateDirectory(RemoteDir);

        if (File.Exists(CertPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    CertPath,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.Exportable);

                if (existing.NotAfter > DateTime.Now.AddDays(7))
                    return existing;

                log.Info("RemoteIdentity: stored certificate is expiring — regenerating");
            }
            catch (Exception ex)
            {
                log.Warn($"RemoteIdentity: could not load stored certificate, regenerating — {ex.Message}");
            }
        }

        var created = CreateSelfSigned();
        try
        {
            File.WriteAllBytes(CertPath, created.Export(X509ContentType.Pkcs12));
            log.Info($"RemoteIdentity: generated server certificate, fingerprint {FingerprintOf(created)}");
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteIdentity: could not persist certificate — {ex.Message}");
        }

        return created;
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=AgentDock-{Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], // server authentication
                critical: false));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        // Round-tripping through PKCS#12 is what makes the private key usable by SslStream
        // on Windows; a cert straight out of CreateSelfSigned can fail at handshake time.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>SHA-256 of the raw certificate, grouped for readability when shown to a human.</summary>
    public static string FingerprintOf(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        var hex = Convert.ToHexString(hash);
        var sb = new StringBuilder(hex.Length + hex.Length / 4);
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(hex, i, Math.Min(4, hex.Length - i));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ known hosts (client)

    /// <summary>The fingerprint previously pinned for a host, or null if never paired.</summary>
    public string? GetPinnedFingerprint(string host)
    {
        try
        {
            if (!File.Exists(KnownHostsPath)) return null;

            foreach (var line in File.ReadAllLines(KnownHostsPath))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && parts[0].Equals(host, StringComparison.OrdinalIgnoreCase))
                    return parts[1].Trim();
            }
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteIdentity: could not read known hosts — {ex.Message}");
        }

        return null;
    }

    public void PinFingerprint(string host, string fingerprint)
    {
        try
        {
            Directory.CreateDirectory(RemoteDir);

            var lines = File.Exists(KnownHostsPath)
                ? File.ReadAllLines(KnownHostsPath).ToList()
                : [];

            lines.RemoveAll(l =>
                l.Split('\t', 2) is { Length: 2 } p &&
                p[0].Equals(host, StringComparison.OrdinalIgnoreCase));

            lines.Add($"{host}\t{fingerprint}");
            File.WriteAllLines(KnownHostsPath, lines);
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteIdentity: could not pin fingerprint — {ex.Message}");
        }
    }
}
