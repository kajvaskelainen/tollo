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
}
