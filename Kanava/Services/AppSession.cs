using Kanava.Models;

namespace Kanava.Services;

/// <summary>Kirjautuneen käyttäjän tila: rajapinta, ladatut listat, suosikit, opas ja soitin.</summary>
public sealed class AppSession
{
    public AppSession(Credentials credentials, AccountInfo account, SettingsStore settings, PlayerService player)
    {
        Credentials = credentials;
        Account = account;
        Settings = settings;
        Player = player;
        Client = new XtreamClient(credentials);
        UserData = new UserDataStore(credentials.ProfileKey);
        Epg = new EpgService(Client, credentials.ProfileKey);
    }

    public Credentials Credentials { get; }
    public AccountInfo Account { get; }
    public SettingsStore Settings { get; }
    public PlayerService Player { get; }
    public XtreamClient Client { get; }
    public UserDataStore UserData { get; }
    public EpgService Epg { get; }

    public List<Category> LiveCategories { get; private set; } = [];
    public List<LiveChannel> LiveChannels { get; private set; } = [];
    public Dictionary<int, LiveChannel> LiveById { get; private set; } = [];

    // ---------- Elokuvat ja sarjat (ladataan vasta tarvittaessa) ----------

    public List<Category> VodCategories { get; private set; } = [];
    public List<VodItem> Vod { get; private set; } = [];
    public List<Category> SeriesCategories { get; private set; } = [];
    public List<SeriesItem> Series { get; private set; } = [];

    private Task? _vodTask, _seriesTask;

    public Task EnsureVodAsync()
    {
        if (_vodTask is null || _vodTask.IsFaulted) _vodTask = LoadVodAsync();
        return _vodTask;
    }

    public Task EnsureSeriesAsync()
    {
        if (_seriesTask is null || _seriesTask.IsFaulted) _seriesTask = LoadSeriesAsync();
        return _seriesTask;
    }

    private async Task LoadVodAsync()
    {
        var cats = Client.GetVodCategoriesAsync();
        var items = Client.GetVodStreamsAsync();
        await Task.WhenAll(cats, items);
        var list = items.Result;
        foreach (var v in list) v.IsFavorite = UserData.IsVodFavorite(v.StreamId);
        list.Sort((a, b) => Nullable.Compare(b.Added, a.Added)); // uusimmat ensin
        VodCategories = cats.Result;
        Vod = list;
    }

    private async Task LoadSeriesAsync()
    {
        var cats = Client.GetSeriesCategoriesAsync();
        var items = Client.GetSeriesAsync();
        await Task.WhenAll(cats, items);
        var list = items.Result;
        foreach (var s in list) s.IsFavorite = UserData.IsSeriesFavorite(s.SeriesId);
        SeriesCategories = cats.Result;
        Series = list;
    }

    public async Task LoadLiveAsync(CancellationToken ct = default)
    {
        var catsTask = Client.GetLiveCategoriesAsync(ct);
        var streamsTask = Client.GetLiveStreamsAsync(ct);
        await Task.WhenAll(catsTask, streamsTask);

        var channels = streamsTask.Result;
        // Järjestys: palvelimen numerointi, sitten nimi
        channels.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (var c in channels) c.IsFavorite = UserData.IsLiveFavorite(c.StreamId);

        LiveCategories = catsTask.Result;
        LiveChannels = channels;
        LiveById = channels.GroupBy(c => c.StreamId).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Hakee kanavalistan uudelleen ja palauttaa yhteenvedon muutoksista.</summary>
    public async Task<string> ReloadLiveAsync()
    {
        var before = LiveChannels.Select(c => c.StreamId).ToHashSet();
        await LoadLiveAsync();
        var added = LiveChannels.Count(c => !before.Contains(c.StreamId));
        var removed = before.Count - LiveChannels.Count(c => before.Contains(c.StreamId));
        _ = Epg.LoadAsync(force: false);
        return $"Kanavalista ladattu: {LiveChannels.Count} kanavaa, {LiveCategories.Count} kategoriaa"
               + (added > 0 ? $", {added} uutta" : "") + (removed > 0 ? $", {removed} poistunut" : "")
               + (added == 0 && removed == 0 ? " – ei muutoksia." : ".");
    }

    // ---------- Piilotus ja järjestys (Live TV, opas, haku ja kanavien hallinta käyttävät näitä) ----------

    /// <summary>Kanava näkyy, jos sitä tai sen kategoriaa ei ole piilotettu.</summary>
    public bool IsVisible(LiveChannel c) => !UserData.IsLiveHidden(c.StreamId) && !UserData.IsLiveCategoryHidden(c.CategoryId);

    public IEnumerable<LiveChannel> VisibleLiveChannels => LiveChannels.Where(IsVisible);

    public IEnumerable<Category> VisibleLiveCategories => LiveCategories.Where(c => !UserData.IsLiveCategoryHidden(c.Id));

    /// <summary>Kaikki live-kategoriat (myös piilotetut) käyttäjän järjestyksessä.</summary>
    public List<Category> OrderedLiveCategories() =>
        UserDataStore.ApplyOrder(LiveCategories, UserData.GetCategoryOrder("live"), c => c.Id);

    /// <summary>
    /// Avaimen ("__all", "__fav" tai kategorian id) kanavat käyttäjän järjestyksessä.
    /// </summary>
    public List<LiveChannel> OrderedChannels(string key, bool includeHidden)
    {
        var list = key switch
        {
            "__all" => UserDataStore.ApplyOrder(LiveChannels, UserData.GetChannelOrder("__all"), c => c.StreamId),
            "__fav" => UserDataStore.ApplyOrder(LiveChannels.Where(c => c.IsFavorite), UserData.FavoriteLiveOrder, c => c.StreamId),
            _ => UserDataStore.ApplyOrder(LiveChannels.Where(c => c.CategoryId == key), UserData.GetChannelOrder(key), c => c.StreamId),
        };
        return includeHidden ? list : list.Where(IsVisible).ToList();
    }

    /// <summary>
    /// Tallentaa kanavajärjestyksen. <paramref name="order"/> voi sisältää vain näkyvät kanavat:
    /// piilotetut pysyvät aiemmilla paikoillaan (eivätkä esim. putoa suosikeista).
    /// </summary>
    public void SaveChannelOrder(string key, IReadOnlyList<int> order)
    {
        var full = OrderedChannels(key, includeHidden: true).Select(c => c.StreamId).ToList();
        UserData.SetChannelOrder(key, UserDataStore.MergeOrder(full, order));
    }

    /// <summary>Tallentaa kategoriajärjestyksen; piilotetut kategoriat pysyvät paikoillaan.</summary>
    public void SaveLiveCategoryOrder(IReadOnlyList<string> order)
    {
        var full = OrderedLiveCategories().Select(c => c.Id).ToList();
        UserData.SetCategoryOrder("live", UserDataStore.MergeOrder(full, order));
    }
}
