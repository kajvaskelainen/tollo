using System.IO;
using System.Text.Json;
using Kanava.Models;

namespace Kanava.Services;

public sealed class UserData
{
    /// <summary>Suosikkikanavat käyttäjän järjestyksessä.</summary>
    public List<int> FavoriteLive { get; set; } = [];
    /// <summary>Live-kategorioiden oma järjestys (kategorian id:t).</summary>
    public List<string> LiveCategoryOrder { get; set; } = [];
    /// <summary>Kanavien oma järjestys kategorioittain (avain: kategorian id tai "__all").</summary>
    public Dictionary<string, List<int>> LiveChannelOrder { get; set; } = [];
    /// <summary>Piilotetut live-kanavat (eivät näy Live TV:ssä, oppaassa eivätkä haussa).</summary>
    public HashSet<int> HiddenLive { get; set; } = [];
    /// <summary>Piilotetut live-kategoriat (piilottavat myös kaikki kategorian kanavat).</summary>
    public HashSet<string> HiddenLiveCategories { get; set; } = [];
    /// <summary>Elokuva- ja sarjakategorioiden oma järjestys (avain: "vod" / "series").</summary>
    public Dictionary<string, List<string>> CategoryOrder { get; set; } = [];
    public HashSet<int> FavoriteVod { get; set; } = [];
    public HashSet<int> FavoriteSeries { get; set; } = [];
    /// <summary>Viimeksi katsotut live-kanavat, uusin ensin.</summary>
    public List<int> LiveHistory { get; set; } = [];
    /// <summary>Keskeneräiset elokuvat ja jaksot (avain "movie:123" / "ep:456").</summary>
    public Dictionary<string, WatchProgress> Progress { get; set; } = [];
    /// <summary>Katsotuiksi merkityt (avain kuten yllä).</summary>
    public HashSet<string> Watched { get; set; } = [];
    /// <summary>TV-oppaan muistutukset.</summary>
    public List<Reminder> Reminders { get; set; } = [];
    /// <summary>Ajastetut, käynnissä olevat ja valmiit tallennukset.</summary>
    public List<Recording> Recordings { get; set; } = [];
}

public sealed class Recording
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int StreamId { get; set; }
    public string ChannelName { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Scheduled, Recording, Done, Failed, Cancelled</summary>
    public string Status { get; set; } = "Scheduled";
    public string? File { get; set; }
    public string? Error { get; set; }
}

public sealed class Reminder
{
    public int StreamId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public bool Notified { get; set; }
}

/// <summary>Elokuvan tai jakson katselukohta "Jatka katsomista" -listaa varten.</summary>
public sealed class WatchProgress
{
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Title { get; set; } = "";
    public int? SeriesId { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;
}

/// <summary>Suosikit ja historia profiilikohtaisesti %AppData%\Tollo\userdata_*.json -tiedostoon.</summary>
public sealed class UserDataStore
{
    private const int MaxHistory = 50;
    private readonly string _file;
    private readonly System.Timers.Timer _saveTimer;
    private readonly object _lock = new();
    private UserData _data = new();

    public event Action? Changed;

    public UserDataStore(string profileKey)
    {
        _file = AppPaths.UserDataFile(profileKey);
        _saveTimer = new System.Timers.Timer(1000) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) => SaveNow();
        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(_file)) ?? new();
        }
        catch { _data = new(); }
    }

    public IReadOnlyList<int> LiveHistory { get { lock (_lock) return _data.LiveHistory.ToList(); } }

    public bool IsLiveFavorite(int id) { lock (_lock) return _data.FavoriteLive.Contains(id); }
    public bool IsVodFavorite(int id) { lock (_lock) return _data.FavoriteVod.Contains(id); }
    public bool IsSeriesFavorite(int id) { lock (_lock) return _data.FavoriteSeries.Contains(id); }

    public void ToggleLiveFavorite(LiveChannel c)
    {
        bool fav;
        lock (_lock)
        {
            fav = !_data.FavoriteLive.Remove(c.StreamId);
            if (fav) _data.FavoriteLive.Add(c.StreamId);
        }
        c.IsFavorite = fav;
        ScheduleSave();
    }

    // ---------- Katselukohta ja katsotut ----------

    /// <summary>Elokuva tai jakso katsottu loppuun tätä osuutta myöten.</summary>
    public const double WatchedThreshold = 0.93;

    public WatchProgress? GetProgress(string key) { lock (_lock) return _data.Progress.GetValueOrDefault(key); }

    public bool IsWatched(string key) { lock (_lock) return _data.Watched.Contains(key); }

    /// <summary>Tallentaa katselukohdan. Yli 93 %: merkitään katsotuksi ja poistetaan jatka-listalta. Alle 1 min ei tallenneta.</summary>
    public void SetProgress(string key, long positionMs, long durationMs, WatchProgress info)
    {
        if (durationMs <= 0) return;
        lock (_lock)
        {
            if ((double)positionMs / durationMs >= WatchedThreshold)
            {
                _data.Progress.Remove(key);
                _data.Watched.Add(key);
            }
            else if (positionMs >= 60_000)
            {
                info.PositionMs = positionMs;
                info.DurationMs = durationMs;
                info.UpdatedUtc = DateTime.UtcNow;
                _data.Progress[key] = info;
                // Pidetään lista kohtuullisena
                if (_data.Progress.Count > 200)
                    foreach (var old in _data.Progress.OrderBy(kv => kv.Value.UpdatedUtc).Take(_data.Progress.Count - 200).Select(kv => kv.Key).ToList())
                        _data.Progress.Remove(old);
            }
            else return;
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    public void SetWatched(string key, bool watched)
    {
        lock (_lock)
        {
            if (watched) { _data.Watched.Add(key); _data.Progress.Remove(key); }
            else _data.Watched.Remove(key);
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    public void ClearProgress(string key)
    {
        lock (_lock) if (!_data.Progress.Remove(key)) return;
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>Keskeneräiset, uusin ensin.</summary>
    public IReadOnlyList<(string Key, WatchProgress Progress)> InProgress()
    {
        lock (_lock) return _data.Progress.OrderByDescending(kv => kv.Value.UpdatedUtc).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ---------- Tallennukset ----------

    public List<Recording> Recordings { get { lock (_lock) return _data.Recordings.ToList(); } }

    public void UpdateRecordings(Action<List<Recording>> change)
    {
        lock (_lock)
        {
            change(_data.Recordings);
            // Historiaa enintään 50 valmista/peruttua
            var old = _data.Recordings.Where(r => r.Status is "Done" or "Failed" or "Cancelled").OrderByDescending(r => r.End).Skip(50).ToList();
            foreach (var r in old) _data.Recordings.Remove(r);
        }
        ScheduleSave();
    }

    // ---------- Varmuuskopio ----------

    public string ExportJson()
    {
        lock (_lock) return JsonSerializer.Serialize(_data);
    }

    /// <summary>Korvaa kaikki käyttäjätiedot varmuuskopiolla ja tallentaa heti.</summary>
    public void Import(UserData data)
    {
        lock (_lock) _data = data;
        SaveNow();
        Changed?.Invoke();
    }

    // ---------- Muistutukset ----------

    public bool IsReminded(int streamId, DateTime start)
    {
        lock (_lock) return _data.Reminders.Any(r => r.StreamId == streamId && r.Start == start);
    }

    /// <summary>Lisää tai poistaa muistutuksen; palauttaa true, jos muistutus on nyt päällä.</summary>
    public bool ToggleReminder(LiveChannel ch, EpgProgram p)
    {
        bool on;
        lock (_lock)
        {
            on = _data.Reminders.RemoveAll(r => r.StreamId == ch.StreamId && r.Start == p.Start) == 0;
            if (on) _data.Reminders.Add(new Reminder { StreamId = ch.StreamId, Start = p.Start, End = p.End, Title = p.Title, ChannelName = ch.Name });
        }
        ScheduleSave();
        return on;
    }

    /// <summary>Palauttaa muistutukset, joiden aika on tullut (merkitään ilmoitetuiksi), ja siivoaa päättyneet.</summary>
    public List<Reminder> TakeDueReminders(DateTime now, TimeSpan lead)
    {
        List<Reminder> due;
        lock (_lock)
        {
            var removed = _data.Reminders.RemoveAll(r => r.End < now);
            due = _data.Reminders.Where(r => !r.Notified && r.Start - lead <= now).ToList();
            foreach (var r in due) r.Notified = true;
            if (due.Count == 0 && removed == 0) return due;
        }
        ScheduleSave();
        return due;
    }

    // ---------- Oma järjestys ----------

    public IReadOnlyList<int> FavoriteLiveOrder { get { lock (_lock) return _data.FavoriteLive.ToList(); } }

    public IReadOnlyList<int>? GetChannelOrder(string key)
    {
        lock (_lock) return _data.LiveChannelOrder.TryGetValue(key, out var l) ? l.ToList() : null;
    }

    public void SetChannelOrder(string key, IEnumerable<int>? ids)
    {
        lock (_lock)
        {
            if (key == "__fav") _data.FavoriteLive = ids?.ToList() ?? _data.FavoriteLive;
            else if (ids is null) _data.LiveChannelOrder.Remove(key);
            else _data.LiveChannelOrder[key] = ids.ToList();
        }
        ScheduleSave();
    }

    /// <summary>Kategorioiden oma järjestys: kind = "live", "vod" tai "series".</summary>
    public IReadOnlyList<string> GetCategoryOrder(string kind)
    {
        lock (_lock)
            return kind == "live" ? _data.LiveCategoryOrder.ToList()
                 : _data.CategoryOrder.TryGetValue(kind, out var l) ? l.ToList() : [];
    }

    public void SetCategoryOrder(string kind, IEnumerable<string>? ids)
    {
        lock (_lock)
        {
            if (kind == "live") _data.LiveCategoryOrder = ids?.ToList() ?? [];
            else if (ids is null) _data.CategoryOrder.Remove(kind);
            else _data.CategoryOrder[kind] = ids.ToList();
        }
        ScheduleSave();
    }

    // ---------- Piilotus ----------

    public bool IsLiveHidden(int id) { lock (_lock) return _data.HiddenLive.Contains(id); }
    public bool IsLiveCategoryHidden(string id) { lock (_lock) return _data.HiddenLiveCategories.Contains(id); }
    public int HiddenLiveCount { get { lock (_lock) return _data.HiddenLive.Count; } }

    public void SetLiveHidden(IEnumerable<int> ids, bool hidden)
    {
        lock (_lock)
            foreach (var id in ids)
                if (hidden) _data.HiddenLive.Add(id); else _data.HiddenLive.Remove(id);
        ScheduleSave();
    }

    public void SetLiveCategoriesHidden(IEnumerable<string> ids, bool hidden)
    {
        lock (_lock)
            foreach (var id in ids)
                if (hidden) _data.HiddenLiveCategories.Add(id); else _data.HiddenLiveCategories.Remove(id);
        ScheduleSave();
    }

    /// <summary>
    /// Yhdistää näkyvien kohteiden uuden järjestyksen koko listan järjestykseen niin, että
    /// piilotetut kohteet pysyvät paikoillaan (näkyvien paikat täytetään uudessa järjestyksessä).
    /// </summary>
    public static List<TKey> MergeOrder<TKey>(IReadOnlyList<TKey> full, IReadOnlyList<TKey> visibleNew) where TKey : notnull
    {
        var visible = new HashSet<TKey>(visibleNew);
        var result = new List<TKey>(full.Count);
        var next = 0;
        foreach (var k in full)
            result.Add(visible.Contains(k) && next < visibleNew.Count ? visibleNew[next++] : k);
        while (next < visibleNew.Count) result.Add(visibleNew[next++]);
        return result;
    }

    /// <summary>Järjestää listan tallennetun id-järjestyksen mukaan; tuntemattomat perään alkuperäisessä järjestyksessä.</summary>
    public static List<T> ApplyOrder<T, TKey>(IEnumerable<T> items, IReadOnlyList<TKey>? order, Func<T, TKey> key) where TKey : notnull
    {
        var list = items.ToList();
        if (order is null || order.Count == 0) return list;
        var rank = new Dictionary<TKey, int>();
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return list.Select((x, i) => (x, r: rank.TryGetValue(key(x), out var r) ? r : int.MaxValue, i))
                   .OrderBy(t => t.r).ThenBy(t => t.i).Select(t => t.x).ToList();
    }
    public void ToggleVodFavorite(VodItem v) => v.IsFavorite = Toggle(_data.FavoriteVod, v.StreamId);
    public void ToggleSeriesFavorite(SeriesItem s) => s.IsFavorite = Toggle(_data.FavoriteSeries, s.SeriesId);

    private bool Toggle(HashSet<int> set, int id)
    {
        bool result;
        lock (_lock) result = set.Add(id) || !set.Remove(id);
        ScheduleSave();
        return result;
    }

    public void AddLiveHistory(int streamId)
    {
        lock (_lock)
        {
            _data.LiveHistory.Remove(streamId);
            _data.LiveHistory.Insert(0, streamId);
            if (_data.LiveHistory.Count > MaxHistory) _data.LiveHistory.RemoveRange(MaxHistory, _data.LiveHistory.Count - MaxHistory);
        }
        ScheduleSave();
    }

    public void ClearHistory()
    {
        lock (_lock) _data.LiveHistory.Clear();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    public void SaveNow()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_data);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* tallennus yritetään uudelleen seuraavan muutoksen yhteydessä */ }
    }
}
