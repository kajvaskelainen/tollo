using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Kanava.Models;

namespace Kanava.Services;

/// <summary>Yksi jäsennetty XMLTV-opas (palveluntarjoajan tai ulkoinen).</summary>
public sealed class EpgGuide
{
    public Dictionary<string, List<EpgProgram>> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Normalisoitu kanavanimi (display-name tai tunniste) → kanavan tunniste.</summary>
    public Dictionary<string, string> NameToId { get; } = new();
    /// <summary>Väljä avain (EpgMatcher.Loose) → tunniste; käytetään, jos tarkka nimi ei osu.</summary>
    public Dictionary<string, string> LooseToId { get; } = new();
    /// <summary>Kanavan tunniste → ensimmäinen näyttönimi.</summary>
    public Dictionary<string, string> DisplayName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime Min { get; set; } = DateTime.MaxValue;
    public DateTime Max { get; set; } = DateTime.MinValue;
    public int TotalProgrammes { get; set; }

    /// <summary>Etsii kanavalle ohjelmat: ensin tunnisteella, sitten nimellä. Palauttaa oppaan kanavan tunnisteen.</summary>
    public string? Match(LiveChannel channel)
    {
        if (!string.IsNullOrEmpty(channel.EpgChannelId))
        {
            if (ById.ContainsKey(channel.EpgChannelId)) return channel.EpgChannelId;
            if (NameToId.TryGetValue(EpgMatcher.Normalize(channel.EpgChannelId), out var byIdName)) return byIdName;
        }
        var key = EpgMatcher.Normalize(channel.Name);
        if (key.Length == 0) return null;
        if (NameToId.TryGetValue(key, out var id)) return id;
        return LooseToId.TryGetValue(EpgMatcher.Loose(key), out var loose) ? loose : null;
    }

    public IReadOnlyList<EpgProgram>? Programs(string id) => ById.TryGetValue(id, out var l) && l.Count > 0 ? l : null;
}

/// <summary>Ulkoisen oppaan testin tulos (asetusten "Testaa"-nappi).</summary>
public sealed class EpgTestResult
{
    public string? Error { get; init; }
    public int SourceChannels { get; init; }
    public int SourceChannelsWithPrograms { get; init; }
    public int TotalProgrammes { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>Lähteen kanavat, joilla on ohjelma juuri nyt (paljastaa vanhentuneen tai väärin ajoitetun oppaan).</summary>
    public int ChannelsWithNow { get; init; }
    public int TestedChannels { get; init; }
    /// <summary>Omat kanavat → lähteen kanavan nimi.</summary>
    public List<(LiveChannel Channel, string SourceName, bool HasNow)> Matched { get; init; } = [];
    /// <summary>Osumista ne, joilla ei nyt ole opasta palveluntarjoajalta.</summary>
    public int NewlyCovered { get; init; }
    public List<LiveChannel> Unmatched { get; init; } = [];
}

/// <summary>
/// Ohjelmaopas: palveluntarjoajan xmltv.php sekä käyttäjän lisäämät ulkoiset XMLTV-lähteet
/// (http(s)-osoite tai paikallinen tiedosto, myös .gz). Ladataan taustalla levyvälimuistiin,
/// päivitetään kerran vuorokaudessa ja jäsennetään suoratoistona (XmlReader).
/// Ulkoinen opas täydentää: sitä käytetään kanaville, joille palveluntarjoajalla ei ole ohjelmia.
/// </summary>
public sealed class EpgService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly IReadOnlyList<EpgProgram> Empty = [];

    private readonly XtreamClient _client;
    private readonly SettingsStore _settings;
    private readonly string _file;
    private EpgGuide? _provider;
    private List<EpgGuide> _external = [];
    private Dictionary<int, IReadOnlyList<EpgProgram>> _resolved = new();
    private int _loading;

    public bool IsLoaded { get; private set; }
    public DateTime? LastUpdated { get; private set; }
    public string Status { get; private set; } = "Opasta ei ole ladattu";

    /// <summary>Laukeaa (taustasäikeessä), kun opas on ladattu tai tila muuttuu.</summary>
    public event Action? Updated;

    public EpgService(XtreamClient client, SettingsStore settings, string profileKey)
    {
        _client = client;
        _settings = settings;
        _file = AppPaths.EpgFile(profileKey);
    }

    public IReadOnlyList<EpgProgram> GetPrograms(LiveChannel channel)
    {
        if (_provider is { } p && !string.IsNullOrEmpty(channel.EpgChannelId) && p.Programs(channel.EpgChannelId) is { } own)
            return own;
        var ext = _external;
        if (ext.Count == 0) return Empty;
        var cache = _resolved;
        if (cache.TryGetValue(channel.StreamId, out var hit)) return hit;
        IReadOnlyList<EpgProgram> found = Empty;
        foreach (var g in ext)
            if (g.Match(channel) is { } id && g.Programs(id) is { } list) { found = list; break; }
        cache[channel.StreamId] = found;
        return found;
    }

    public (DateTime start, DateTime end) Range { get; private set; }

    /// <summary>Lataa oppaat taustalla. force = ohita välimuisti.</summary>
    public Task LoadAsync(bool force = false) => Task.Run(async () =>
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var notes = new List<string>();
        string? providerError = null;
        try
        {
            // 1) Palveluntarjoajan opas
            var fi = new FileInfo(_file);
            if (fi.Exists && fi.Length > 0 && _provider is null)
            {
                SetStatus("Luetaan opasta välimuistista…");
                Publish(provider: Parse(_file), external: null);
                LastUpdated = fi.LastWriteTime;
            }
            // M3U-soittolistalla ei välttämättä ole opasta (url-tvg) – silloin vain ulkoiset lähteet
            var hasProviderGuide = _client.XmltvUrl().Length > 0;
            if (hasProviderGuide && (force || !(fi.Exists && fi.Length > 0 && DateTime.Now - fi.LastWriteTime < MaxAge)))
            {
                try
                {
                    SetStatus("Ladataan ohjelmaopasta…");
                    await DownloadAsync(_client.XmltvUrl(), _file, CancellationToken.None);
                    SetStatus("Käsitellään ohjelmaopasta…");
                    Publish(provider: Parse(_file), external: null);
                    LastUpdated = DateTime.Now;
                }
                catch (Exception ex)
                {
                    providerError = _provider is null ? $"palveluntarjoajan opas ei latautunut ({ex.Message})" : "palveluntarjoajan oppaan päivitys epäonnistui";
                }
            }

            // 2) Ulkoiset oppaat
            var sources = _settings.Settings.EpgSources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            var guides = new List<EpgGuide>();
            foreach (var src in sources)
            {
                var file = SourceFile(src.Url);
                try
                {
                    var sf = new FileInfo(file);
                    if (force || !(sf.Exists && sf.Length > 0 && DateTime.Now - sf.LastWriteTime < MaxAge))
                    {
                        SetStatus($"Ladataan ulkoista opasta ({src.DisplayName})…");
                        try { await DownloadAsync(src.Url, file, CancellationToken.None); }
                        catch when (File.Exists(file)) { notes.Add($"{src.DisplayName}: päivitys epäonnistui, käytetään vanhaa"); }
                    }
                    SetStatus($"Käsitellään ulkoista opasta ({src.DisplayName})…");
                    guides.Add(Parse(file));
                }
                catch (Exception ex) { notes.Add($"{src.DisplayName}: {ex.Message}"); }
            }
            Publish(provider: null, external: guides);
            // Ilman palveluntarjoajan opasta toimivat ulkoiset lähteet riittävät – ei pitkää virheilmoitusta
            if (providerError is not null)
                notes.Insert(0, guides.Count > 0 && _provider is null ? "ei palveluntarjoajan opasta" : providerError);
            if (guides.Count > 0) LastUpdated ??= DateTime.Now;

            var main = LastUpdated is { } t ? $"Opas päivitetty {t:d.M. HH:mm}" : "Opasta ei ole ladattu";
            if (guides.Count > 0) main += guides.Count == 1 ? " · 1 ulkoinen lähde" : $" · {guides.Count} ulkoista lähdettä";
            SetStatus(notes.Count > 0 ? $"{main} ({string.Join("; ", notes)})" : main);
        }
        catch (Exception ex)
        {
            SetStatus(IsLoaded ? $"Oppaan päivitys epäonnistui ({ex.Message}) – näytetään välimuisti" : $"Oppaan lataus epäonnistui: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _loading, 0); }
    });

    /// <summary>Vaihtaa käytössä olevat oppaat (null = ennallaan) ja tyhjentää nimihakujen välimuistin.</summary>
    private void Publish(EpgGuide? provider, List<EpgGuide>? external)
    {
        if (provider is not null) _provider = provider;
        if (external is not null) _external = external;
        _resolved = new();
        var all = new List<EpgGuide>(_external);
        if (_provider is not null) all.Add(_provider);
        var withData = all.Where(g => g.ById.Count > 0).ToList();
        Range = withData.Count > 0 ? (withData.Min(g => g.Min), withData.Max(g => g.Max)) : (DateTime.Now, DateTime.Now);
        IsLoaded = true;
        Updated?.Invoke();
    }

    private void SetStatus(string s)
    {
        Status = s;
        Updated?.Invoke();
    }

    // ---------- Ulkoisen lähteen testaus ----------

    /// <summary>
    /// Lataa lähteen ja vertaa sitä omiin kanaviin vaikuttamatta käytössä olevaan oppaaseen.
    /// Ladattu tiedosto jää välimuistiin, joten lähteen lisääminen testin jälkeen on nopeaa.
    /// </summary>
    public async Task<EpgTestResult> TestAsync(string url, IReadOnlyList<LiveChannel> channels, CancellationToken ct = default)
    {
        try
        {
            var file = SourceFile(url);
            await DownloadAsync(url, file, ct);
            var guide = await Task.Run(() => Parse(file), ct);
            var now = DateTime.Now;
            bool HasNow(IReadOnlyList<EpgProgram>? l) => l is not null && l.Any(p => p.IsNow(now));

            var matched = new List<(LiveChannel, string, bool)>();
            var unmatched = new List<LiveChannel>();
            var newly = 0;
            foreach (var c in channels)
            {
                if (guide.Match(c) is { } id && guide.Programs(id) is { } list)
                {
                    matched.Add((c, guide.DisplayName.GetValueOrDefault(id, id), HasNow(list)));
                    if (_provider is null || string.IsNullOrEmpty(c.EpgChannelId) || _provider.Programs(c.EpgChannelId) is null) newly++;
                }
                else unmatched.Add(c);
            }
            return new EpgTestResult
            {
                SourceChannels = guide.DisplayName.Count,
                SourceChannelsWithPrograms = guide.ById.Count(kv => kv.Value.Count > 0),
                TotalProgrammes = guide.TotalProgrammes,
                From = guide.TotalProgrammes > 0 ? guide.Min : null,
                To = guide.TotalProgrammes > 0 ? guide.Max : null,
                ChannelsWithNow = guide.ById.Values.Count(l => HasNow(l)),
                TestedChannels = channels.Count,
                Matched = matched,
                NewlyCovered = newly,
                Unmatched = unmatched,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new EpgTestResult { Error = ex.Message }; }
    }

    // ---------- Lataus ja jäsennys ----------

    private static string SourceFile(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..16].ToLowerInvariant();
        return Path.Combine(AppPaths.Cache, $"epgx_{hash}.xml");
    }

    /// <summary>Lataa http(s)-osoitteesta tai kopioi paikallisesta tiedostosta; purkaa gzipin.</summary>
    private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        url = url.Trim();
        var tmp = dest + ".download";
        await using (var dst = File.Create(tmp))
        {
            Stream src;
            HttpResponseMessage? resp = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                resp = await XtreamClient.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new IOException($"palvelin vastasi {(int)resp.StatusCode} {resp.ReasonPhrase}");
                src = await resp.Content.ReadAsStreamAsync(ct);
            }
            else
            {
                var path = uri is { IsFile: true } ? uri.LocalPath : url;
                if (!File.Exists(path)) throw new FileNotFoundException("Tiedostoa ei löydy: " + path);
                src = File.OpenRead(path);
            }
            using (resp)
            await using (src)
            {
                // gzip tunnistetaan sisällöstä (1F 8B), ei päätteestä
                var buffered = new BufferedStream(src, 65536);
                var head = new byte[2];
                var n = await buffered.ReadAsync(head, ct);
                await using var rest = new ConcatStream(head.AsMemory(0, n), buffered);
                if (n == 2 && head[0] == 0x1F && head[1] == 0x8B)
                {
                    await using var gz = new GZipStream(rest, CompressionMode.Decompress);
                    await gz.CopyToAsync(dst, ct);
                }
                else await rest.CopyToAsync(dst, ct);
            }
        }
        if (new FileInfo(tmp).Length < 64) { File.Delete(tmp); throw new IOException("lähde palautti tyhjän oppaan"); }
        File.Move(tmp, dest, overwrite: true);
    }

    private static EpgGuide Parse(string path)
    {
        // Pidetään muistissa vain kiinnostava ikkuna: 6 h taaksepäin – 3 vrk eteenpäin
        var from = DateTime.Now.AddHours(-6);
        var to = DateTime.Now.AddDays(3);
        var g = new EpgGuide();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name == "channel") ReadChannel(reader, g);
            else if (reader.Name == "programme") ReadProgramme(reader, g, from, to);
        }

        foreach (var list in g.ById.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            // Poistetaan päällekkäisyydet (osa palvelimista toistaa ohjelmia)
            for (var i = list.Count - 1; i > 0; i--)
                if (list[i].Start < list[i - 1].End && list[i].Start == list[i - 1].Start) list.RemoveAt(i);
        }
        return g;
    }

    private static void ReadChannel(XmlReader reader, EpgGuide g)
    {
        var id = reader.GetAttribute("id");
        if (string.IsNullOrEmpty(id)) return;
        AddName(g, id, id);
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.Name != "display-name") continue;
            var name = sub.ReadElementContentAsString().Trim();
            if (name.Length == 0) continue;
            g.DisplayName.TryAdd(id, name);
            AddName(g, name, id);
        }
        g.DisplayName.TryAdd(id, id);
    }

    private static void AddName(EpgGuide g, string name, string id)
    {
        var key = EpgMatcher.Normalize(name);
        if (key.Length == 0) return;
        g.NameToId.TryAdd(key, id);
        g.LooseToId.TryAdd(EpgMatcher.Loose(key), id);
    }

    private static void ReadProgramme(XmlReader reader, EpgGuide g, DateTime from, DateTime to)
    {
        var ch = reader.GetAttribute("channel");
        var start = ParseTime(reader.GetAttribute("start"));
        var stop = ParseTime(reader.GetAttribute("stop"));
        if (ch is null || start is null || stop is null || stop <= start) return;
        g.TotalProgrammes++;
        if (start < g.Min) g.Min = start.Value;
        if (stop > g.Max) g.Max = stop.Value;
        if (!g.ById.TryGetValue(ch, out var list)) g.ById[ch] = list = [];
        if (stop <= from || start >= to) return;

        string title = "", desc = "";
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element) continue;
                if (sub.Name == "title" && title.Length == 0) title = sub.ReadElementContentAsString().Trim();
                else if (sub.Name == "desc" && desc.Length == 0) desc = sub.ReadElementContentAsString().Trim();
            }
        }
        list.Add(new EpgProgram { Start = start.Value, End = stop.Value, Title = title, Description = desc });
    }

    /// <summary>XMLTV-aika: "20261112183000 +0200" (vyöhyke vapaaehtoinen).</summary>
    public static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 12) return null;
        s = s.Trim();
        var digits = s.Length >= 14 ? s[..14] : s[..12].PadRight(14, '0');
        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;
        var rest = s.Length > 14 ? s[14..].Trim() : "";
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-') &&
            int.TryParse(rest.AsSpan(1, 2), out var hh) && int.TryParse(rest.AsSpan(3, 2), out var mm))
        {
            var offset = new TimeSpan(hh, mm, 0);
            if (rest[0] == '-') offset = -offset;
            return new DateTimeOffset(dt, offset).LocalDateTime;
        }
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
    }

    /// <summary>Lukee ensin jo luetut tavut ja sitten loput virrasta (gzip-tunnistusta varten).</summary>
    private sealed class ConcatStream(ReadOnlyMemory<byte> head, Stream tail) : Stream
    {
        private ReadOnlyMemory<byte> _head = head;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_head.Length > 0)
            {
                var n = Math.Min(buffer.Length, _head.Length);
                _head.Span[..n].CopyTo(buffer);
                _head = _head[n..];
                return n;
            }
            return tail.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_head.Length > 0) return new ValueTask<int>(Read(buffer.Span));
            return tail.ReadAsync(buffer, ct);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) tail.Dispose(); base.Dispose(disposing); }
    }
}
