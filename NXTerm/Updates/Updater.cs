using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NXTerm-Updater/{CurrentVersionText}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    static HttpClient Http => LazyHttp.Value;

    /// <summary>"owner/repo" baked in at build time (set by the release workflow), or null for local builds.</summary>
    public static string? Repo =>
        Environment.GetEnvironmentVariable("NXTERM_UPDATE_REPO") is { Length: > 0 } env ? env
        : typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value is { Length: > 0 } repo ? repo : null;

    static string ApiBase => Environment.GetEnvironmentVariable("NXTERM_UPDATE_API") is { Length: > 0 } api ? api.TrimEnd('/') : "https://api.github.com";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>The latest published release, or null when the repository has none yet.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        var repo = Repo ?? throw new InvalidOperationException("No update source is configured for this build.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.GetAsync($"{ApiBase}/repos/{repo}/releases/latest", timeout.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(timeout.Token));
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
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
                if (name.Equals("NXTerm.exe", StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                if (pick == null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pick = a;
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
        if (r.AssetUrl == null) throw new InvalidOperationException("This release has no NXTerm.exe to download.");
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
