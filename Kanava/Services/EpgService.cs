using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Xml;
using Kanava.Models;

namespace Kanava.Services;

/// <summary>
/// Koko ohjelmaopas xmltv.php:sta. Ladataan taustalla levyvälimuistiin,
/// päivitetään kerran vuorokaudessa ja jäsennetään suoratoistona (XmlReader).
/// </summary>
public sealed class EpgService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly IReadOnlyList<EpgProgram> Empty = [];

    private readonly XtreamClient _client;
    private readonly string _file;
    private Dictionary<string, List<EpgProgram>> _byChannel = new(StringComparer.OrdinalIgnoreCase);
    private int _loading;

    public bool IsLoaded { get; private set; }
    public DateTime? LastUpdated { get; private set; }
    public string Status { get; private set; } = "Opasta ei ole ladattu";

    /// <summary>Laukeaa (taustasäikeessä), kun opas on ladattu tai tila muuttuu.</summary>
    public event Action? Updated;

    public EpgService(XtreamClient client, string profileKey)
    {
        _client = client;
        _file = AppPaths.EpgFile(profileKey);
    }

    public IReadOnlyList<EpgProgram> GetPrograms(LiveChannel channel)
    {
        if (string.IsNullOrEmpty(channel.EpgChannelId)) return Empty;
        var map = _byChannel;
        return map.TryGetValue(channel.EpgChannelId, out var list) ? list : Empty;
    }

    public (DateTime start, DateTime end) Range { get; private set; }

    /// <summary>Lataa oppaan taustalla. force = ohita välimuisti.</summary>
    public Task LoadAsync(bool force = false) => Task.Run(async () =>
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var fi = new FileInfo(_file);
            var fresh = fi.Exists && fi.Length > 0 && DateTime.Now - fi.LastWriteTime < MaxAge;

            // Näytetään vanha välimuisti heti, vaikka se olisi vanhentunut
            if (fi.Exists && fi.Length > 0 && !IsLoaded)
            {
                SetStatus("Luetaan opasta välimuistista…");
                Parse(_file);
                LastUpdated = fi.LastWriteTime;
                SetStatus($"Opas päivitetty {LastUpdated:d.M. HH:mm}");
            }

            if (!fresh || force)
            {
                SetStatus("Ladataan ohjelmaopasta…");
                var tmp = _file + ".download";
                using (var resp = await XtreamClient.Http.GetAsync(_client.XmltvUrl(), HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(tmp);
                    await src.CopyToAsync(dst);
                }
                if (new FileInfo(tmp).Length < 64) throw new IOException("Palvelin palautti tyhjän oppaan.");
                File.Move(tmp, _file, overwrite: true);
                SetStatus("Käsitellään ohjelmaopasta…");
                Parse(_file);
                LastUpdated = DateTime.Now;
                SetStatus($"Opas päivitetty {LastUpdated:d.M. HH:mm}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(IsLoaded ? $"Oppaan päivitys epäonnistui ({ex.Message}) – näytetään välimuisti" : $"Oppaan lataus epäonnistui: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _loading, 0); }
    });

    private void SetStatus(string s)
    {
        Status = s;
        Updated?.Invoke();
    }

    private void Parse(string path)
    {
        // Pidetään muistissa vain kiinnostava ikkuna: 6 h taaksepäin – 3 vrk eteenpäin
        var from = DateTime.Now.AddHours(-6);
        var to = DateTime.Now.AddDays(3);
        var map = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
        DateTime min = DateTime.MaxValue, max = DateTime.MinValue;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(path, settings);
        while (reader.ReadToFollowing("programme"))
        {
            var ch = reader.GetAttribute("channel");
            var start = ParseTime(reader.GetAttribute("start"));
            var stop = ParseTime(reader.GetAttribute("stop"));
            if (ch is null || start is null || stop is null || stop <= from || start >= to || stop <= start)
            {
                continue;
            }

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

            if (!map.TryGetValue(ch, out var list)) map[ch] = list = [];
            list.Add(new EpgProgram { Start = start.Value, End = stop.Value, Title = title, Description = desc });
            if (start < min) min = start.Value;
            if (stop > max) max = stop.Value;
        }

        foreach (var list in map.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            // Poistetaan päällekkäisyydet (osa palvelimista toistaa ohjelmia)
            for (var i = list.Count - 1; i > 0; i--)
                if (list[i].Start < list[i - 1].End && list[i].Start == list[i - 1].Start) list.RemoveAt(i);
        }

        _byChannel = map;
        Range = map.Count > 0 ? (min, max) : (DateTime.Now, DateTime.Now);
        IsLoaded = true;
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
}
