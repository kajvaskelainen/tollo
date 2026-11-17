using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kanava.Models;

namespace Kanava.Services;

public sealed class XtreamException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Verkko- tai aikakatkaisuvirhe, jonka uudelleenyritys voi korjata (ei esim. väärä salasana).</summary>
    public bool IsTransient => InnerException is HttpRequestException or TaskCanceledException;
}

/// <summary>Xtream Codes -rajapinnan asiakas (player_api.php).</summary>
public class XtreamClient
{
    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Tollo/1.0");
        return http;
    }

    public Credentials Credentials { get; }

    public XtreamClient(Credentials credentials) => Credentials = credentials;

    /// <summary>Oikea asiakas tilin tyypin mukaan: Xtream Codes tai M3U-soittolista.</summary>
    public static XtreamClient Create(Credentials credentials) =>
        credentials.IsM3u ? new M3uClient(credentials) : new XtreamClient(credentials);

    private string Api(string action, string extra = "")
    {
        var c = Credentials;
        var url = $"{c.BaseUrl}/player_api.php?username={Uri.EscapeDataString(c.Username)}&password={Uri.EscapeDataString(c.Password)}";
        if (!string.IsNullOrEmpty(action)) url += "&action=" + action;
        return url + extra;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new XtreamException("Palvelin hylkäsi tunnukset.");
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (XtreamException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException ex) { throw new XtreamException("Palvelin ei vastannut ajoissa.", ex); }
        catch (HttpRequestException ex) { throw new XtreamException($"Yhteysvirhe: {ex.Message}", ex); }
        catch (JsonException ex) { throw new XtreamException("Palvelimen vastaus ei ollut kelvollista JSONia.", ex); }
    }

    // ---------- Kirjautuminen ----------

    public virtual async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api(""), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user_info", out var ui))
            throw new XtreamException("Palvelin ei vastannut Xtream Codes -muodossa.");
        if (ui.Int("auth") != 1)
            throw new XtreamException("Virheellinen käyttäjätunnus tai salasana.");

        var formats = new List<string>();
        if (ui.TryGetProperty("allowed_output_formats", out var f) && f.ValueKind == JsonValueKind.Array)
            formats.AddRange(f.EnumerateArray().Select(x => x.ToString()));

        var tz = root.TryGetProperty("server_info", out var si) && si.ValueKind == JsonValueKind.Object ? si.Str("timezone") : "";

        return new AccountInfo
        {
            ServerTimeZone = tz,
            Username = ui.Str("username"),
            Status = ui.Str("status"),
            ExpiresAt = ui.UnixTime("exp_date"),
            MaxConnections = ui.Int("max_connections"),
            ActiveConnections = ui.Int("active_cons"),
            IsTrial = ui.Int("is_trial") == 1,
            AllowedFormats = formats,
        };
    }

    // ---------- Kategoriat ----------

    public virtual Task<List<Category>> GetLiveCategoriesAsync(CancellationToken ct = default) => GetCategoriesAsync("get_live_categories", ct);
    public virtual Task<List<Category>> GetVodCategoriesAsync(CancellationToken ct = default) => GetCategoriesAsync("get_vod_categories", ct);
    public virtual Task<List<Category>> GetSeriesCategoriesAsync(CancellationToken ct = default) => GetCategoriesAsync("get_series_categories", ct);

    private async Task<List<Category>> GetCategoriesAsync(string action, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api(action), ct).ConfigureAwait(false);
        return doc.RootElement.Items()
            .Select(e => new Category { Id = e.Str("category_id"), Name = e.Str("category_name").Trim() })
            .Where(c => c.Id.Length > 0)
            .ToList();
    }

    // ---------- Live ----------

    public virtual async Task<List<LiveChannel>> GetLiveStreamsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_live_streams"), ct).ConfigureAwait(false);
        return doc.RootElement.Items()
            .Select(e => new LiveChannel
            {
                StreamId = e.Int("stream_id"),
                Number = e.Int("num"),
                Name = e.Str("name").Trim(),
                Logo = e.StrOrNull("stream_icon"),
                EpgChannelId = e.StrOrNull("epg_channel_id"),
                CategoryId = e.Str("category_id"),
                HasArchive = e.Int("tv_archive") == 1,
                ArchiveDays = e.Int("tv_archive_duration"),
            })
            .Where(c => c.StreamId > 0)
            .ToList();
    }

    /// <summary>Kanavan "nyt / seuraavaksi" -tiedot.</summary>
    public virtual async Task<List<EpgProgram>> GetShortEpgAsync(int streamId, int limit = 4, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_short_epg", $"&stream_id={streamId}&limit={limit}"), ct).ConfigureAwait(false);
        var list = new List<EpgProgram>();
        if (!doc.RootElement.TryGetProperty("epg_listings", out var listings)) return list;
        foreach (var e in listings.Items())
        {
            var start = e.UnixTime("start_timestamp");
            var end = e.UnixTime("stop_timestamp");
            if (start is null || end is null) continue;
            list.Add(new EpgProgram
            {
                Start = start.Value,
                End = end.Value,
                Title = DecodeB64(e.Str("title")),
                Description = DecodeB64(e.Str("description")),
            });
        }
        return list.OrderBy(p => p.Start).ToList();
    }

    private static string DecodeB64(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)).Trim(); }
        catch (FormatException) { return s; }
    }

    // ---------- VOD ----------

    public virtual async Task<List<VodItem>> GetVodStreamsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_vod_streams"), ct).ConfigureAwait(false);
        return doc.RootElement.Items()
            .Select(e => new VodItem
            {
                StreamId = e.Int("stream_id"),
                Name = e.Str("name").Trim(),
                Poster = e.StrOrNull("stream_icon"),
                CategoryId = e.Str("category_id"),
                ContainerExtension = e.Str("container_extension", "mp4"),
                Rating = e.Dbl("rating"),
                Added = e.UnixTime("added"),
            })
            .Where(v => v.StreamId > 0)
            .ToList();
    }

    public virtual async Task<VodDetails> GetVodInfoAsync(int vodId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_vod_info", $"&vod_id={vodId}"), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        root.TryGetProperty("info", out var info);
        root.TryGetProperty("movie_data", out var md);
        return new VodDetails
        {
            Plot = info.Str("plot", info.Str("description")),
            Cast = info.Str("cast", info.Str("actors")),
            Director = info.Str("director"),
            Genre = info.Str("genre"),
            ReleaseDate = info.Str("releasedate", info.Str("release_date")),
            Duration = info.Str("duration"),
            Backdrop = NullIfEmpty(info.FirstStr("backdrop_path")),
            ContainerExtension = md.Str("container_extension"),
        };
    }

    // ---------- Sarjat ----------

    public virtual async Task<List<SeriesItem>> GetSeriesAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_series"), ct).ConfigureAwait(false);
        return doc.RootElement.Items()
            .Select(e => new SeriesItem
            {
                SeriesId = e.Int("series_id"),
                Name = e.Str("name").Trim(),
                Cover = e.StrOrNull("cover"),
                CategoryId = e.Str("category_id"),
                Plot = e.Str("plot"),
                Genre = e.Str("genre"),
                ReleaseDate = e.Str("releaseDate", e.Str("release_date")),
                Rating = e.Dbl("rating"),
            })
            .Where(s => s.SeriesId > 0)
            .ToList();
    }

    public virtual async Task<SeriesDetails> GetSeriesInfoAsync(int seriesId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_series_info", $"&series_id={seriesId}"), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        root.TryGetProperty("info", out var info);

        var seasonNames = new Dictionary<int, string>();
        if (root.TryGetProperty("seasons", out var seasonsEl))
            foreach (var s in seasonsEl.Items())
            {
                var n = s.Int("season_number");
                var name = s.Str("name");
                if (n > 0 && !string.IsNullOrWhiteSpace(name)) seasonNames[n] = name;
            }

        var seasons = new List<Season>();
        if (root.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in eps.EnumerateObject())
            {
                int.TryParse(prop.Name, out var sn);
                var episodes = prop.Value.Items().Select(e =>
                {
                    e.TryGetProperty("info", out var ei);
                    return new Episode
                    {
                        Id = e.Str("id"),
                        Number = e.Int("episode_num"),
                        Season = e.Int("season", sn),
                        Title = e.Str("title").Trim(),
                        ContainerExtension = e.Str("container_extension", "mp4"),
                        Plot = ei.Str("plot"),
                        Duration = ei.Str("duration"),
                        Image = NullIfEmpty(ei.Str("movie_image")),
                    };
                }).OrderBy(e => e.Number).ToList();
                seasons.Add(new Season
                {
                    Number = sn,
                    Name = seasonNames.TryGetValue(sn, out var nm) && !nm.StartsWith("Season", StringComparison.OrdinalIgnoreCase)
                        ? nm : $"Kausi {sn}",
                    Episodes = episodes,
                });
            }
        }

        return new SeriesDetails
        {
            Plot = info.Str("plot"),
            Cast = info.Str("cast"),
            Genre = info.Str("genre"),
            Backdrop = NullIfEmpty(info.FirstStr("backdrop_path")),
            Seasons = seasons.OrderBy(s => s.Number).ToList(),
        };
    }

    // ---------- URL:t ----------

    public virtual string LiveUrl(int streamId, string format = "ts")
    {
        var c = Credentials;
        return $"{c.BaseUrl}/live/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{streamId}.{format}";
    }

    public virtual string MovieUrl(int streamId, string ext)
    {
        var c = Credentials;
        return $"{c.BaseUrl}/movie/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{streamId}.{ext}";
    }

    public virtual string EpisodeUrl(string episodeId, string ext)
    {
        var c = Credentials;
        return $"{c.BaseUrl}/series/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{episodeId}.{ext}";
    }

    /// <summary>Palvelimen aikavyöhyke (jälkikatselun aloitusaika ilmoitetaan palvelimen ajassa).</summary>
    public TimeZoneInfo? ServerTimeZone { get; set; }

    /// <summary>
    /// Jälkikatselu (catch-up / timeshift): /timeshift/{user}/{pass}/{kesto min}/{YYYY-MM-DD:HH-MM}/{id}.ts
    /// </summary>
    public virtual string TimeshiftUrl(int streamId, DateTime startLocal, int durationMinutes)
    {
        var c = Credentials;
        var start = ServerTimeZone is { } tz ? TimeZoneInfo.ConvertTime(startLocal, TimeZoneInfo.Local, tz) : startLocal;
        return $"{c.BaseUrl}/timeshift/{Uri.EscapeDataString(c.Username)}/{Uri.EscapeDataString(c.Password)}/{durationMinutes}/{start:yyyy-MM-dd:HH-mm}/{streamId}.ts";
    }

    public virtual string XmltvUrl()
    {
        var c = Credentials;
        return $"{c.BaseUrl}/xmltv.php?username={Uri.EscapeDataString(c.Username)}&password={Uri.EscapeDataString(c.Password)}";
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
