using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Kanava.Models;

namespace Kanava.Services;

/// <summary>
/// M3U/M3U8-soittolista (http(s)-osoite tai paikallinen tiedosto) Xtream-asiakkaan tilalla.
/// Tarjoaa live-kanavat ja kategoriat (group-title); ohjelmaopas url-tvg-osoitteesta tai ulkoisista lähteistä.
/// Elokuvia, sarjoja ja jälkikatselua ei ole.
/// </summary>
public sealed partial class M3uClient : XtreamClient
{
    private sealed record Entry(int Id, string Name, string Url, string? Logo, string? TvgId, string Group, int Number);
    private sealed record Playlist(List<Entry> Entries, string? TvgUrl, DateTime Loaded);

    // Sama soittolista ladataan kirjautumisessa ja istunnossa – pidetään hetki muistissa
    private static readonly Dictionary<string, Playlist> Cache = new(StringComparer.OrdinalIgnoreCase);
    private Playlist? _playlist;

    public M3uClient(Credentials credentials) : base(credentials) { }

    [GeneratedRegex(@"([\w-]+)=""([^""]*)""")]
    private static partial Regex Attr();

    private async Task<Playlist> LoadAsync(CancellationToken ct)
    {
        if (_playlist is not null) return _playlist;
        var key = Credentials.ServerUrl.Trim();
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached) && DateTime.Now - cached.Loaded < TimeSpan.FromMinutes(5))
                return _playlist = cached;

        string text;
        try
        {
            if (Uri.TryCreate(key, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                text = await Http.GetStringAsync(uri, ct).ConfigureAwait(false);
            else
            {
                var path = Uri.TryCreate(key, UriKind.Absolute, out var f) && f.IsFile ? f.LocalPath : key;
                if (!File.Exists(path)) throw new XtreamException("Soittolistaa ei löydy: " + path);
                text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
        }
        catch (XtreamException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) { throw new XtreamException("Soittolistan lataus epäonnistui: " + ex.Message, ex); }
        catch (TaskCanceledException ex) { throw new XtreamException("Soittolistan palvelin ei vastannut ajoissa.", ex); }
        catch (Exception ex) { throw new XtreamException("Soittolistaa ei voitu lukea: " + ex.Message); }

        var pl = Parse(text);
        if (pl.Entries.Count == 0) throw new XtreamException("Soittolistasta ei löytynyt yhtään kanavaa (onko se M3U-muotoinen?).");
        lock (Cache) Cache[key] = pl;
        return _playlist = pl;
    }

    private static Playlist Parse(string text)
    {
        var entries = new List<Entry>();
        string? tvgUrl = null;
        Dictionary<string, string>? attrs = null;
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var a = Attrs(line);
                tvgUrl = a.GetValueOrDefault("url-tvg") ?? a.GetValueOrDefault("x-tvg-url");
                if (tvgUrl?.Contains(',') == true) tvgUrl = tvgUrl.Split(',')[0];
            }
            else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                attrs = Attrs(line);
                // Nimi on viimeisen lainausmerkkien ulkopuolisen pilkun jälkeen
                var comma = LastUnquotedComma(line);
                name = comma >= 0 ? line[(comma + 1)..].Trim() : attrs.GetValueOrDefault("tvg-name") ?? "";
            }
            else if (line.StartsWith('#')) continue;
            else
            {
                // Elokuvat ja sarjat (Xtream-tyylisessä listassa) jätetään pois live-listalta
                if (!IsVod(line))
                {
                    var a = attrs ?? new();
                    var n = string.IsNullOrWhiteSpace(name) ? a.GetValueOrDefault("tvg-name") ?? $"Kanava {entries.Count + 1}" : name;
                    int.TryParse(a.GetValueOrDefault("tvg-chno"), out var chno);
                    entries.Add(new Entry(entries.Count + 1, n, line, Empty(a.GetValueOrDefault("tvg-logo")), Empty(a.GetValueOrDefault("tvg-id")),
                        a.GetValueOrDefault("group-title") is { Length: > 0 } g ? g : "Muut", chno > 0 ? chno : entries.Count + 1));
                }
                attrs = null;
                name = null;
            }
        }
        return new Playlist(entries, Empty(tvgUrl), DateTime.Now);
    }

    private static bool IsVod(string url)
    {
        var u = url.ToLowerInvariant();
        return u.Contains("/movie/") || u.Contains("/series/") ||
               u.EndsWith(".mp4") || u.EndsWith(".mkv") || u.EndsWith(".avi");
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Dictionary<string, string> Attrs(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr().Matches(line)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static int LastUnquotedComma(string line)
    {
        var inQuote = false;
        var last = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ',' && !inQuote) last = i;
        }
        return last;
    }

    private Dictionary<string, int> GroupIds(Playlist pl)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in pl.Entries) d.TryAdd(e.Group, d.Count + 1);
        return d;
    }

    // ---------- Xtream-rajapinnan korvaajat ----------

    public override async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        var pl = await LoadAsync(ct);
        return new AccountInfo
        {
            Username = Credentials.Name.Length > 0 ? Credentials.Name : "M3U",
            Status = $"M3U-soittolista · {pl.Entries.Count} kanavaa",
        };
    }

    public override async Task<List<Category>> GetLiveCategoriesAsync(CancellationToken ct = default)
    {
        var pl = await LoadAsync(ct);
        return GroupIds(pl).Select(kv => new Category { Id = kv.Value.ToString(), Name = kv.Key }).ToList();
    }

    public override async Task<List<LiveChannel>> GetLiveStreamsAsync(CancellationToken ct = default)
    {
        var pl = await LoadAsync(ct);
        var groups = GroupIds(pl);
        return pl.Entries.Select(e => new LiveChannel
        {
            StreamId = e.Id,
            Number = e.Number,
            Name = e.Name,
            Logo = e.Logo,
            EpgChannelId = e.TvgId,
            CategoryId = groups[e.Group].ToString(),
        }).ToList();
    }

    public override Task<List<EpgProgram>> GetShortEpgAsync(int streamId, int limit = 4, CancellationToken ct = default) => Task.FromResult(new List<EpgProgram>());
    public override Task<List<Category>> GetVodCategoriesAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public override Task<List<Category>> GetSeriesCategoriesAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public override Task<List<VodItem>> GetVodStreamsAsync(CancellationToken ct = default) => Task.FromResult(new List<VodItem>());
    public override Task<List<SeriesItem>> GetSeriesAsync(CancellationToken ct = default) => Task.FromResult(new List<SeriesItem>());
    public override Task<VodDetails> GetVodInfoAsync(int vodId, CancellationToken ct = default) => Task.FromResult(new VodDetails());
    public override Task<SeriesDetails> GetSeriesInfoAsync(int seriesId, CancellationToken ct = default) => Task.FromResult(new SeriesDetails());

    public override string LiveUrl(int streamId, string format = "ts") =>
        _playlist?.Entries.FirstOrDefault(e => e.Id == streamId)?.Url ?? "";

    /// <summary>Soittolistan url-tvg; tyhjä = ei palveluntarjoajan opasta (ulkoiset lähteet toimivat).</summary>
    public override string XmltvUrl() => _playlist?.TvgUrl ?? "";

    public override string TimeshiftUrl(int streamId, DateTime startLocal, int durationMinutes) => LiveUrl(streamId);
}
