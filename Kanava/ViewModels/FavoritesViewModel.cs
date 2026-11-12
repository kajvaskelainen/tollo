using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>Suosikit-sivu: live-kanavat, elokuvat ja sarjat sekä viimeksi katsotut kanavat.</summary>
public partial class FavoritesViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;

    [ObservableProperty] private IReadOnlyList<LiveChannel> _liveFavorites = [];
    [ObservableProperty] private IReadOnlyList<LiveChannel> _history = [];
    [ObservableProperty] private IReadOnlyList<VodItem> _movieFavorites = [];
    [ObservableProperty] private IReadOnlyList<SeriesItem> _seriesFavorites = [];
    [ObservableProperty] private bool _isEmpty;

    public FavoritesViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        Refresh();
        _ = LoadVodAndSeriesAsync();
    }

    private async Task LoadVodAndSeriesAsync()
    {
        try
        {
            await Task.WhenAll(_session.EnsureVodAsync(), _session.EnsureSeriesAsync());
            Refresh();
        }
        catch { /* elokuvat/sarjat eivät ole pakollisia suosikkisivulle */ }
    }

    public void Refresh()
    {
        LiveFavorites = _session.LiveChannels.Where(c => c.IsFavorite).ToList();
        History = _session.UserData.LiveHistory.Select(id => _session.LiveById.GetValueOrDefault(id)).OfType<LiveChannel>().Take(24).ToList();
        MovieFavorites = _session.Vod.Where(v => v.IsFavorite).ToList();
        SeriesFavorites = _session.Series.Where(s => s.IsFavorite).ToList();
        IsEmpty = LiveFavorites.Count == 0 && MovieFavorites.Count == 0 && SeriesFavorites.Count == 0;
    }

    [RelayCommand]
    private void PlayLive(LiveChannel? channel)
    {
        if (channel is null || _main.LiveTv is null) return;
        _main.Navigate(Section.LiveTv);
        _main.LiveTv.SelectedChannel = channel;
    }

    [RelayCommand]
    private void OpenMovie(VodItem? item)
    {
        if (item is null) return;
        _main.OpenMovie(item);
    }

    [RelayCommand]
    private void OpenSeries(SeriesItem? item)
    {
        if (item is null) return;
        _main.OpenSeries(item);
    }

    [RelayCommand]
    private void ToggleLive(LiveChannel? c) { if (c is null) return; _session.UserData.ToggleLiveFavorite(c); Refresh(); }

    [RelayCommand]
    private void ToggleMovie(VodItem? v) { if (v is null) return; _session.UserData.ToggleVodFavorite(v); Refresh(); }

    [RelayCommand]
    private void ToggleSeries(SeriesItem? s) { if (s is null) return; _session.UserData.ToggleSeriesFavorite(s); Refresh(); }

    [RelayCommand]
    private void ClearHistory()
    {
        _session.UserData.ClearHistory();
        Refresh();
    }
}
