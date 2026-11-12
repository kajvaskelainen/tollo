using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kanava.Services;

public sealed record UpdateInfo(Version Version, string PageUrl, string? Notes, string AssetUrl, string AssetName, long Size, string? Sha256);

/// <summary>
/// Päivitykset GitHubin Releases-sivulta (uusin julkaisu). Asennus:
/// zip ladataan ja tarkistetaan (SHA-256), puretaan, ja ohjelmakansion tiedostot vaihdetaan
/// nimeämällä käytössä olevat tiedostot *.old-päätteisiksi (Windows sallii käytössä olevan
/// tiedoston uudelleennimeämisen). Lopuksi uusi versio käynnistetään ja vanhat poistetaan
/// seuraavalla käynnistyksellä.
/// </summary>
public static class UpdateService
{
    public const string Repo = "kajvaskelainen/tollo";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
    private const string ExeName = "Tollo.exe";
    private const string OldSuffix = ".old";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Tollo/{CurrentVersion}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static string InstallDir => AppContext.BaseDirectory;

    /// <summary>Voiko ohjelma päivittää itsensä (julkaistu versio ja kirjoitusoikeus omaan kansioon).</summary>
    public static bool CanSelfUpdate
    {
        get
        {
#if DEBUG
            return false;
#else
            if (!File.Exists(Path.Combine(InstallDir, ExeName))) return false;
            try
            {
                var probe = Path.Combine(InstallDir, $"~update-test-{Environment.ProcessId}.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
#endif
        }
    }

    /// <summary>Hakee uusimman julkaisun. Palauttaa null, jos se ei ole nykyistä uudempi.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = Normalize(v);
        if (v <= CurrentVersion) return null;

        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:"))
                sha = ds["sha256:".Length..];
            return new UpdateInfo(v,
                r.GetProperty("html_url").GetString() ?? ReleasesPage,
                r.TryGetProperty("body", out var b) ? b.GetString() : null,
                a.GetProperty("browser_download_url").GetString()!,
                name, a.GetProperty("size").GetInt64(), sha);
        }
        return null; // julkaisussa ei (vielä) ole Windows-zipiä
    }

    /// <summary>Lataa, tarkistaa ja asentaa päivityksen. Palauttaa uuden exen polun.</summary>
    public static async Task<string> InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "Tollo-update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, info.AssetName);

        // 1) Lataus
        using (var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(0.9 * done / total);
            }
        }

        // 2) Eheys: GitHubin ilmoittama SHA-256
        if (info.Sha256 is { } expected)
        {
            await using var fs = File.OpenRead(zip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Ladatun päivityksen tarkistussumma ei täsmää. Päivitystä ei asennettu.");
        }

        // 3) Purku ja sisällön tarkistus
        var extracted = Path.Combine(work, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted), ct);
        var exe = Directory.GetFiles(extracted, ExeName, SearchOption.AllDirectories)
                           .OrderBy(p => p.Length).FirstOrDefault()
                  ?? throw new InvalidDataException("Päivityspaketista puuttuu " + ExeName + ".");
        var root = Path.GetDirectoryName(exe)!;
        progress?.Report(0.95);

        // 4) Tiedostojen vaihto
        await Task.Run(() => Swap(root, InstallDir), ct);
        progress?.Report(1);
        try { Directory.Delete(work, true); } catch { }
        return Path.Combine(InstallDir, ExeName);
    }

    private static void Swap(string from, string to)
    {
        var moved = new List<string>();   // kohteet, joista on *.old-varmuuskopio
        var added = new List<string>();   // uudet tiedostot (poistetaan virheessä)
        try
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                    moved.Add(target);
                }
                else added.Add(target);
                File.Copy(file, target);
            }
        }
        catch
        {
            // Palautetaan alkuperäinen tila
            foreach (var t in added) try { File.Delete(t); } catch { }
            foreach (var t in moved)
                try { if (File.Exists(t)) File.Delete(t); File.Move(t + OldSuffix, t); } catch { }
            throw;
        }
    }

    /// <summary>Poistaa edellisen päivityksen jättämät *.old-tiedostot (käynnistyksen jälkeen taustalla).</summary>
    public static void CleanupOldFiles()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // vanha prosessi ehtii sulkeutua
            try
            {
                foreach (var f in Directory.EnumerateFiles(InstallDir, "*" + OldSuffix, SearchOption.AllDirectories))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        });
    }

    public static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    public static void OpenPage(string? url = null) =>
        Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true });
}
