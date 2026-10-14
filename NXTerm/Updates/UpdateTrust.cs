using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NXTerm.Updates;

/// <summary>
/// Decides whether the update server's certificate is genuine. Windows decides first; when it only fails
/// because it has no root certificate for the chain (PCs that do not receive root updates lack the newer
/// ones), the chain is checked again against the public roots that travel inside NXTerm.exe.
/// Nothing is relaxed: the name, dates and signatures must all be right, and a certificate from an
/// HTTPS-inspecting proxy or antivirus is refused by both checks.
/// </summary>
public static class UpdateTrust
{
    static readonly Lazy<X509Certificate2Collection> LazyRoots = new(() =>
    {
        var roots = new X509Certificate2Collection();
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("update-roots.pem");
        if (stream == null) return roots;
        using var reader = new StreamReader(stream);
        roots.ImportFromPem(reader.ReadToEnd());
        return roots;
    });

    /// <summary>The root certificates bundled with the program (see Resources\update-roots.pem).</summary>
    public static X509Certificate2Collection BundledRoots => LazyRoots.Value;

    /// <summary>The verdict for one TLS handshake, given what Windows concluded about it.</summary>
    public static bool Accept(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        // A wrong host name or a missing certificate is never rescued, only "Windows does not know this root".
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is not X509Certificate2 leaf || chain == null) return false;
        if (chain.ChainStatus.Any(s => s.Status is not (X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain))) return false;

        var sent = new X509Certificate2Collection();
        sent.AddRange(chain.ChainPolicy.ExtraStore);  // the intermediate certificates the server sent
        foreach (var element in chain.ChainElements) sent.Add(element.Certificate);
        return ChainsTo(leaf, sent, BundledRoots);
    }

    /// <summary>True when <paramref name="leaf"/> is a valid server certificate signed, through <paramref name="intermediates"/>, by one of <paramref name="roots"/>.</summary>
    public static bool ChainsTo(X509Certificate2 leaf, X509Certificate2Collection intermediates, X509Certificate2Collection roots)
    {
        if (roots.Count == 0) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;  // only these roots count, not the Windows store
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;     // same as the normal HttpClient check
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // must be a TLS server certificate
        try { return chain.Build(leaf); }
        catch (CryptographicException) { return false; }
    }
}
