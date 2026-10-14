using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace NXTerm.Updates;

public sealed record ReleaseInfo(
    Version Version, string Tag, string Title, string Notes, string PageUrl,
    string? AssetUrl, string? AssetName, long AssetSize, string? Sha256);

/// <summary>Checks GitHub Releases for a newer NXTerm.exe and replaces the running executable with it.</summary>
public static class Updater
{
    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    static readonly Lazy<HttpClient> LazyHttp = new(() =>
    {
        // UpdateTrust validates the certificate; the callback also remembers why it was refused,
        // because the exception itself just says "The SSL connection could not be established".
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            if (UpdateTrust.Accept(certificate, chain, errors))
            {
                LastCertificateProblem = null;
                return true;
            }
            var status = chain == null ? "" : string.Join(", ", chain.ChainStatus.Select(s => s.Status).Distinct());
            var issuer = (certificate as X509Certificate2)?.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            LastCertificateProblem = errors + (status.Length > 0 ? $" ({status})" : "") +
                                     (string.IsNullOrEmpty(issuer) ? "" : $", issued by \"{issuer}\"");
            return false;
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NXTerm-Updater/{CurrentVersionText}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    static HttpClient Http => LazyHttp.Value;

    /// <summary>Why the server certificate was refused on the last request, or null when it was accepted.</summary>
    public static string? LastCertificateProblem { get; private set; }

    /// <summary>Turns a failed update request into a message that says what went wrong and what to try.</summary>
    public static string DescribeFailure(Exception ex, string? certificateProblem)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        string text, hint;
        if (certificateProblem != null)
        {
            text = "The security certificate of the server was not accepted.\n\nCertificate check: " + certificateProblem;
            hint = certificateProblem.Contains("NotTimeValid")
                ? "Check that the date and time of this PC are correct."
                : certificateProblem.Contains("NameMismatch")
                    ? "Something between this PC and GitHub answered instead of GitHub, for example a Wi-Fi login page, a proxy or a DNS filter."
                    : "Neither Windows nor NXTerm's own list of public root certificates recognises who signed it. Most likely an antivirus " +
                      "or company proxy is inspecting HTTPS traffic (the \"issued by\" name above shows who). If that name is a public " +
                      "certificate authority, run Windows Update to refresh the root certificates of this PC.";
            return text + "\n\n" + hint;
        }

        // Without a certificate verdict the useful detail is the innermost error (the outer one only says "see inner exception").
        text = ex.Message.Replace(", see inner exception", "").TrimEnd('.') + ".";
        if (inner != ex && !ex.Message.Contains(inner.Message)) text += "\n" + inner.Message;
        if (ex is TaskCanceledException or OperationCanceledException) hint = "GitHub did not answer in time. Check the internet connection and try again.";
        else if (inner is System.Net.Sockets.SocketException or IOException)
            hint = "The connection was cut before it could be secured. Check the internet connection, firewall, VPN or proxy and try again.";
        else hint = "Check the internet connection and try again.";
        return text + "\n\n" + hint;
    }

    /// <summary>"owner/repo" baked in at build time (set by the release workflow), or null for local builds.</summary>
    public static string? Repo =>
        Environment.GetEnvironmentVariable("NXTERM_UPDATE_REPO") is { Length: > 0 } env ? env
        : typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value is { Length: > 0 } repo ? repo : null;

    public const string StandardAsset = "NXTerm.exe", StandaloneAsset = "NXTerm-standalone.exe";

    /// <summary>
    /// True for the "standalone" download that carries its own .NET runtime. It must only update to another
    /// standalone build: the small NXTerm.exe would not start on a PC without the .NET Desktop Runtime.
    /// </summary>
    public static bool IsStandalone { get; } =
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Flavor")?.Value == "standalone";

    static string ApiBase => Environment.GetEnvironmentVariable("NXTERM_UPDATE_API") is { Length: > 0 } api ? api.TrimEnd('/') : "https://api.github.com";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>The latest published release, or null when the repository has none yet.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        var repo = Repo ?? throw new InvalidOperationException("No update source is configured for this build.");
        LastCertificateProblem = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.GetAsync($"{ApiBase}/repos/{repo}/releases/latest", timeout.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <param name="standalone">Which download to look for; defaults to the kind of the running program.</param>
    public static ReleaseInfo? ParseRelease(string json, bool? standalone = null)
    {
        string wanted = standalone ?? IsStandalone ? StandaloneAsset : StandardAsset;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? assetUrl = null, assetName = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            JsonElement? pick = null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
            }
            if (pick is { } p)
            {
                assetName = p.GetProperty("name").GetString();
                assetUrl = p.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = p.TryGetProperty("size", out var s) && s.TryGetInt64(out long l) ? l : 0;
                if (p.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..];
            }
        }
        string Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var title = Str("name");
        return new ReleaseInfo(version, tag, title.Length > 0 ? title : tag, Str("body"), Str("html_url"), assetUrl, assetName, size, sha);
    }

    /// <summary>Parses "v1.2.3", "1.2", "1.2.3-beta" and similar tags.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (Version.TryParse(s, out var v)) { version = Normalize(v); return true; }
        if (int.TryParse(s, out int major)) { version = new Version(major, 0, 0); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static bool IsNewer(ReleaseInfo r) => r.Version > CurrentVersion;

    /// <summary>Downloads the release executable, verifies it and swaps it with the running NXTerm.exe.</summary>
    public static async Task InstallAsync(ReleaseInfo r, IProgress<long>? progress, CancellationToken ct)
    {
        if (r.AssetUrl == null) throw new InvalidOperationException("This release has no matching program file to download.");
        LastCertificateProblem = null;
        var exe = ExePath;
        var tmp = exe + ".download";
        var old = exe + ".old";
        try
        {
            using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    total += n;
                    progress?.Report(total);
                }
            }

            if (r.AssetSize > 0 && new FileInfo(tmp).Length != r.AssetSize)
                throw new IOException("The download is incomplete (size does not match).");
            if (r.Sha256 != null)
            {
                await using var fs = File.OpenRead(tmp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The download is corrupted (SHA-256 does not match).");
            }
            if (!IsWindowsExe(tmp)) throw new IOException("The downloaded file is not a Windows program.");

            // Windows lets a running .exe be renamed (not overwritten), so move it aside first.
            File.Move(exe, old, overwrite: true);
            try { File.Move(tmp, exe); }
            catch
            {
                File.Move(old, exe);
                throw;
            }
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    static bool IsWindowsExe(string path)
    {
        Span<byte> mz = stackalloc byte[2];
        using var fs = File.OpenRead(path);
        return fs.Read(mz) == 2 && mz[0] == 'M' && mz[1] == 'Z';
    }

    /// <summary>Removes the previous executable left behind by an update.</summary>
    public static void CleanupOldVersion() => TryDelete(ExePath + ".old");

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void StartNewInstance(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    public static void OpenInBrowser(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
