using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public enum Section { Search, LiveTv, Movies, Series, Favorites, Settings, ChannelManager }

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
        StartReminders(session);
        Recordings?.Dispose();
        var recs = Recordings = new RecordingService(session);
        live.IsRecording = id => recs.ActiveFor(id) is not null;
        recs.Changed += live.RefreshRecording;
        CurrentSection = Section.LiveTv;
        IsLoggedIn = true;

        _ = session.Epg.LoadAsync();
    }

    [RelayCommand]
    private void Logout()
    {
        Recordings?.Dispose();
        Recordings = null;
        _reminderTimer?.Stop();
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
        // Kanavien hallinnan muutokset (piilotus, järjestys, uusi lista) näkyviin Live TV:hen
        if (CurrentPage is ChannelManagerViewModel { IsDirty: true }) LiveTv?.Reload();
        else if (CurrentPage is SettingsViewModel { ChannelsReloaded: true } settings)
        {
            settings.ChannelsReloaded = false;
            LiveTv?.Reload();
        }
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
            Section.Settings => _settingsVm ??= new SettingsViewModel(s, Updates, this),
            Section.Favorites => new FavoritesViewModel(s, this),
            Section.ChannelManager => new ChannelManagerViewModel(s, this),
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
    public event Action<PlaybackRequest>? PlayVodRequested;

    /// <summary>Tallennukset (kirjautumisen ajan).</summary>
    [ObservableProperty] private RecordingService? _recordings;

    /// <summary>TV-oppaan muistutus erääntyi (MainWindow näyttää ilmoituksen).</summary>
    public event Action<Reminder>? ReminderDue;
    private System.Windows.Threading.DispatcherTimer? _reminderTimer;

    private void StartReminders(AppSession session)
    {
        _reminderTimer?.Stop();
        _reminderTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _reminderTimer.Tick += (_, _) =>
        {
            if (Session != session) { _reminderTimer?.Stop(); return; }
            foreach (var r in session.UserData.TakeDueReminders(DateTime.Now, TimeSpan.FromMinutes(5))) ReminderDue?.Invoke(r);
        };
        _reminderTimer.Start();
    }

    /// <summary>Muistutuksen "Katso"-nappi: Live TV:hen ja kanavalle.</summary>
    public void WatchReminder(Reminder r)
    {
        if (Session?.LiveById.TryGetValue(r.StreamId, out var ch) != true || LiveTv is null) return;
        if (CurrentSection != Section.LiveTv) Navigate(Section.LiveTv);
        LiveTv.SelectAndPlay(ch!);
    }

    public void PlayVod(PlaybackRequest request)
    {
        // Vain yksi striimi kerrallaan (monet palvelut rajoittavat yhteyksiä)
        _player.Stop(_player.Preview);
        PlayVodRequested?.Invoke(request);
    }

    /// <summary>Kutsutaan, kun VOD-soitin suljetaan.</summary>
    public void OnVodClosed()
    {
        if (CurrentSection == Section.LiveTv) LiveTv?.Resume();
    }

    public void Shutdown()
    {
        Recordings?.Dispose();
        Session?.UserData.SaveNow();
    }
}
