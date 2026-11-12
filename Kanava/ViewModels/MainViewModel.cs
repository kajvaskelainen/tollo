using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public enum Section { Search, LiveTv, Movies, Series, Favorites, Settings }

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly PlayerService _player;

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private AppSession? _session;
    [ObservableProperty] private LiveTvViewModel? _liveTv;
    [ObservableProperty] private Section _currentSection = Section.LiveTv;

    public LoginViewModel Login { get; }
    public PlayerService Player => _player;
    public SettingsStore Settings => _settings;

    public MainViewModel(SettingsStore settings, PlayerService player)
    {
        _settings = settings;
        _player = player;
        Login = new LoginViewModel(settings, OnLoggedInAsync);
        Updates = new UpdateViewModel(settings);
    }

    public UpdateViewModel Updates { get; }

    /// <summary>Automaattinen kirjautuminen tallennetuilla tunnuksilla.</summary>
    public async Task StartAsync(Credentials? commandLine)
    {
        _ = Updates.StartupCheckAsync();
        var creds = commandLine ?? _settings.GetSavedCredentials();
        if (creds is not null) await Login.TryLoginAsync(creds, save: commandLine is not null, automatic: commandLine is null);
    }

    private async Task OnLoggedInAsync(Credentials creds, AccountInfo account)
    {
        var session = new AppSession(creds, account, _settings, _player);
        await session.LoadLiveAsync();

        var live = new LiveTvViewModel(session);
        live.Load();
        session.Epg.Updated += () => Application.Current?.Dispatcher.BeginInvoke(live.OnGuideLoaded);

        Session = session;
        LiveTv = live;
        CurrentSection = Section.LiveTv;
        IsLoggedIn = true;

        _ = session.Epg.LoadAsync();
    }

    [RelayCommand]
    private void Logout()
    {
        _player.Stop(_player.Preview);
        Session?.UserData.SaveNow();
        _settings.ClearPassword();
        LiveTv?.Suspend();
        LiveTv?.Playback.Dispose();
        IsLoggedIn = false;
        LiveTv = null; // ennen osion vaihtoa, ettei esikatselu käynnisty uudelleen
        Session = null;
        CurrentSection = Section.LiveTv;
        CurrentPage = null;
        _movies = null; _series = null; _search = null; _settingsVm = null;
    }

    /// <summary>Muiden kuin Live TV -osioiden näkymämalli (DataTemplate valitsee näkymän).</summary>
    [ObservableProperty] private object? _currentPage;

    partial void OnCurrentSectionChanged(Section oldValue, Section newValue)
    {
        CurrentPage = newValue == Section.LiveTv ? null : CreatePage(newValue);
        if (LiveTv is null) return;
        if (oldValue == Section.LiveTv && newValue != Section.LiveTv) LiveTv.Suspend();
        else if (newValue == Section.LiveTv && oldValue != Section.LiveTv) LiveTv.Resume();
    }

    // Osioiden näkymämallit säilytetään, jotta valinnat ja vierityskohta pysyvät
    private MoviesViewModel? _movies;
    private SeriesViewModel? _series;
    private SearchViewModel? _search;
    private SettingsViewModel? _settingsVm;

    private object? CreatePage(Section section)
    {
        var s = Session;
        if (s is null) return null;
        return section switch
        {
            Section.Movies => _movies ??= new MoviesViewModel(s, this),
            Section.Series => _series ??= new SeriesViewModel(s, this),
            Section.Search => _search ??= new SearchViewModel(s, this),
            Section.Settings => _settingsVm ??= new SettingsViewModel(s, Updates),
            Section.Favorites => new FavoritesViewModel(s, this),
            _ => null,
        };
    }

    [RelayCommand]
    public void Navigate(Section section) => CurrentSection = section;

    public void OpenMovie(VodItem item)
    {
        Navigate(Section.Movies);
        _movies?.Open(item);
    }

    public void OpenSeries(SeriesItem item)
    {
        Navigate(Section.Series);
        _series?.Open(item);
    }

    /// <summary>Pyyntö avata VOD-soitin (MainWindow avaa ikkunan).</summary>
    public event Action<string, string>? PlayVodRequested;

    public void PlayVod(string url, string title)
    {
        // Vain yksi striimi kerrallaan (monet palvelut rajoittavat yhteyksiä)
        _player.Stop(_player.Preview);
        PlayVodRequested?.Invoke(url, title);
    }

    /// <summary>Kutsutaan, kun VOD-soitin suljetaan.</summary>
    public void OnVodClosed()
    {
        if (CurrentSection == Section.LiveTv) LiveTv?.Resume();
    }

    public void Shutdown()
    {
        Session?.UserData.SaveNow();
    }
}
