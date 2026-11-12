using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class LiveTvViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _epgCts;
    private List<LiveChannel> _all = [];

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private IReadOnlyList<LiveChannel> _channels = [];
    [ObservableProperty] private LiveChannel? _selectedChannel;
    [ObservableProperty] private LiveChannel? _playingChannel;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedTab; // 0 = Kanavakortit, 1 = TV-opas

    [ObservableProperty] private EpgProgram? _nowProgram;
    [ObservableProperty] private EpgProgram? _nextProgram;
    [ObservableProperty] private double _nowProgress;
    [ObservableProperty] private DateTime _now = DateTime.Now;
    [ObservableProperty] private string _guideStatus = "";
    [ObservableProperty] private bool _isFullscreen;
    /// <summary>Kasvaa aina kun opas päivittyy (EPG-ruudukko piirtää uudelleen).</summary>
    [ObservableProperty] private int _guideVersion;

    public LiveTvViewModel(AppSession session)
    {
        _session = session;
        Playback = new PlaybackViewModel(session.Player.Preview, session.Player.LibVLC, session.Settings);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    public AppSession Session => _session;

    /// <summary>Esikatselusoittimen hallinta (ääni, raidat, lähetystiedot).</summary>
    public PlaybackViewModel Playback { get; }

    public void Load()
    {
        _all = _session.LiveChannels;
        Categories.Clear();
        Categories.Add(new Category { Id = "__history", Name = "Historia", Special = SpecialCategory.History });
        Categories.Add(new Category { Id = "__fav", Name = "Suosikit", Special = SpecialCategory.Favorites });
        Categories.Add(new Category { Id = "__all", Name = "Kaikki kanavat", Special = SpecialCategory.All });
        foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
        SelectedCategory = Categories[2];
    }

    private CategoryOrderer? _categoryOrder;
    public CategoryOrderer CategoryOrder => _categoryOrder ??= new(Categories, _session.UserData, "live", () => _session.LiveCategories)
    {
        GetSelected = () => SelectedCategory,
        SetSelected = c => SelectedCategory = c,
    };

    partial void OnSelectedCategoryChanged(Category? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private ObservableCollection<LiveChannel> _channelList = [];

    /// <summary>Tallennetun järjestyksen avain valitulle kategorialle (null = ei järjestettävissä).</summary>
    private string? OrderKey => SelectedCategory switch
    {
        null => null,
        { Special: SpecialCategory.History } => null,
        { Special: SpecialCategory.Favorites } => "__fav",
        { Special: SpecialCategory.All } => "__all",
        var c => c.Id,
    };

    public void ApplyFilter()
    {
        IEnumerable<LiveChannel> q;
        var cat = SelectedCategory;
        var ud = _session.UserData;
        if (cat is null || cat.Special == SpecialCategory.All) q = UserDataStore.ApplyOrder(_all, ud.GetChannelOrder("__all"), c => c.StreamId);
        else if (cat.Special == SpecialCategory.Favorites) q = UserDataStore.ApplyOrder(_all.Where(c => c.IsFavorite), ud.FavoriteLiveOrder, c => c.StreamId);
        else if (cat.Special == SpecialCategory.History)
        {
            var byId = _session.LiveById;
            q = ud.LiveHistory.Select(id => byId.GetValueOrDefault(id)).OfType<LiveChannel>();
        }
        else q = UserDataStore.ApplyOrder(_all.Where(c => c.CategoryId == cat.Id), ud.GetChannelOrder(cat.Id), c => c.StreamId);

        var s = SearchText.Trim();
        if (s.Length > 0) q = q.Where(c => c.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        _channelList = new ObservableCollection<LiveChannel>(q);
        Channels = _channelList;
        OnPropertyChanged(nameof(CanReorderChannels));
    }

    // ---------- Järjestely ----------

    /// <summary>Järjestystila: klikkaus ei käynnistä toistoa, kortteja ja kategorioita voi raahata.</summary>
    [ObservableProperty] private bool _isReorderMode;

    public bool CanReorderChannels => OrderKey is not null && SearchText.Trim().Length == 0;

    partial void OnIsReorderModeChanged(bool value)
    {
        if (value) SelectedTab = 0;
    }

    [RelayCommand]
    private void ToggleReorder() => IsReorderMode = !IsReorderMode;

    /// <summary>Siirtää kanavan kohteen paikalle (raahaus).</summary>
    public void MoveChannel(LiveChannel item, LiveChannel? target)
    {
        if (!CanReorderChannels) return;
        var from = _channelList.IndexOf(item);
        var to = target is null ? _channelList.Count - 1 : _channelList.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _channelList.Move(from, to);
        SaveChannelOrder();
    }

    [RelayCommand]
    private void MoveChannelToTop(LiveChannel? c) => MoveChannelTo(c, 0);

    [RelayCommand]
    private void MoveChannelToBottom(LiveChannel? c) => MoveChannelTo(c, _channelList.Count - 1);

    private void MoveChannelTo(LiveChannel? c, int index)
    {
        if (c is null || !CanReorderChannels) return;
        var from = _channelList.IndexOf(c);
        if (from < 0 || from == index) return;
        _channelList.Move(from, index);
        SaveChannelOrder();
    }

    private void SaveChannelOrder()
    {
        if (OrderKey is not { } key) return;
        if (key == "__fav")
        {
            _session.UserData.SetChannelOrder(key, _channelList.Select(c => c.StreamId));
            return;
        }
        // Tallennetaan koko kategorian järjestys (lista on suodattamaton, kun järjestely on sallittu)
        _session.UserData.SetChannelOrder(key, _channelList.Select(c => c.StreamId));
    }

    [RelayCommand]
    private void ResetChannelOrder()
    {
        if (OrderKey is not { } key) return;
        if (key == "__fav")
            _session.UserData.SetChannelOrder(key, _all.Where(c => c.IsFavorite).Select(c => c.StreamId));
        else
            _session.UserData.SetChannelOrder(key, null);
        ApplyFilter();
    }

    partial void OnSelectedChannelChanged(LiveChannel? value)
    {
        if (value is not null && !IsReorderMode) Play(value);
    }

    /// <summary>Käynnistää kanavan esikatselun.</summary>
    public void Play(LiveChannel channel)
    {
        if (PlayingChannel == channel) return;
        PlayingChannel = channel;
        Playback.Reset();
        _session.Player.Play(_session.Player.Preview, _session.Client.LiveUrl(channel.StreamId, _session.Settings.Settings.LiveFormat));
        _session.UserData.AddLiveHistory(channel.StreamId);
        _ = LoadNowNextAsync(channel);
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen && PlayingChannel is not null;

    /// <summary>Tuplaklikkaus kanavaan: toista ja siirry koko näyttöön.</summary>
    [RelayCommand]
    private void PlayFullscreen(LiveChannel? channel)
    {
        if (channel is null) return;
        SelectedChannel = channel;
        Play(channel);
        IsFullscreen = true;
    }

    /// <summary>Pysäyttää esikatselun (esim. kun poistutaan Live TV -näkymästä).</summary>
    public void Suspend()
    {
        IsFullscreen = false;
        _session.Player.Stop(_session.Player.Preview);
    }

    public void Resume()
    {
        // Suosikit/historia ovat voineet muuttua muualla
        if (SelectedCategory?.Special is SpecialCategory.Favorites or SpecialCategory.History) ApplyFilter();
        if (PlayingChannel is { } ch)
            _session.Player.Play(_session.Player.Preview, _session.Client.LiveUrl(ch.StreamId, _session.Settings.Settings.LiveFormat));
    }

    [RelayCommand]
    private void ToggleFavorite(LiveChannel? channel)
    {
        if (channel is null) return;
        _session.UserData.ToggleLiveFavorite(channel);
        if (SelectedCategory?.Special == SpecialCategory.Favorites)
        {
            // Ei koko listan uudelleenrakennusta: poistetaan/lisätään vain kyseinen kanava
            if (!channel.IsFavorite) _channelList.Remove(channel);
            else if (!_channelList.Contains(channel)) _channelList.Add(channel);
        }
    }

    private async Task LoadNowNextAsync(LiveChannel channel)
    {
        _epgCts?.Cancel();
        var cts = _epgCts = new CancellationTokenSource();
        // Näytetään heti XMLTV-välimuistin tiedot, jos ne on jo ladattu
        UpdateNowNextFromGuide(channel);
        try
        {
            var list = await _session.Client.GetShortEpgAsync(channel.StreamId, 4, cts.Token);
            if (cts.IsCancellationRequested || PlayingChannel != channel || list.Count == 0) return;
            SetNowNext(list);
        }
        catch { /* lyhyt EPG ei ole kriittinen */ }
    }

    private void UpdateNowNextFromGuide(LiveChannel channel)
    {
        var list = _session.Epg.GetPrograms(channel);
        SetNowNext(list);
    }

    private void SetNowNext(IReadOnlyList<EpgProgram> list)
    {
        var now = DateTime.Now;
        var idx = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].IsNow(now)) { idx = i; break; }
        NowProgram = idx >= 0 ? list[idx] : null;
        NextProgram = idx >= 0 && idx + 1 < list.Count ? list[idx + 1] : list.FirstOrDefault(p => p.Start > now);
        NowProgress = NowProgram?.Progress(now) ?? 0;
    }

    /// <summary>Päivittää kanavakorttien "nyt"-ohjelmat oppaasta.</summary>
    private void UpdateCurrentPrograms()
    {
        if (!_session.Epg.IsLoaded) return;
        var now = DateTime.Now;
        foreach (var ch in _all)
        {
            var cur = ch.Current;
            if (cur is null || !cur.IsNow(now)) cur = FindCurrent(_session.Epg.GetPrograms(ch), now);
            ch.Current = cur;
            ch.CurrentProgress = cur?.Progress(now) ?? 0;
        }
    }

    public static EpgProgram? FindCurrent(IReadOnlyList<EpgProgram> list, DateTime now)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var p = list[mid];
            if (p.End <= now) lo = mid + 1;
            else if (p.Start > now) hi = mid - 1;
            else return p;
        }
        return null;
    }

    private void Tick()
    {
        Now = DateTime.Now;
        UpdateCurrentPrograms();
        if (NowProgram is not null)
        {
            if (Now >= NowProgram.End && PlayingChannel is not null)
            {
                var ch = PlayingChannel;
                PlayingChannel = null; // pakottaa uudelleenhaun
                PlayingChannel = ch;
                _ = LoadNowNextAsync(ch);
            }
            else NowProgress = NowProgram.Progress(Now);
        }
    }

    /// <summary>Kutsutaan, kun XMLTV-opas on ladattu taustalla.</summary>
    public void OnGuideLoaded()
    {
        GuideStatus = _session.Epg.Status;
        UpdateCurrentPrograms();
        GuideVersion++;
        if (PlayingChannel is not null && NowProgram is null) UpdateNowNextFromGuide(PlayingChannel);
    }
}
