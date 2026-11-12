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
