using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>Haku kaikesta sisällöstä: live-kanavat, elokuvat ja sarjat.</summary>
public partial class SearchViewModel : ObservableObject
{
    private const int MaxPerGroup = 40;
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<LiveChannel> _channels = [];
    [ObservableProperty] private IReadOnlyList<VodItem> _movies = [];
    [ObservableProperty] private IReadOnlyList<SeriesItem> _series = [];
    [ObservableProperty] private string _status = "Kirjoita vähintään 2 merkkiä.";

    public SearchViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        // Elokuvat ja sarjat taustalla, jotta haku löytää ne
        _ = Task.WhenAll(session.EnsureVodAsync(), session.EnsureSeriesAsync()).ContinueWith(_ => { },
            TaskScheduler.Default);
    }

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string text)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var q = text.Trim();
        if (q.Length < 2)
        {
            Channels = []; Movies = []; Series = [];
            Status = "Kirjoita vähintään 2 merkkiä.";
            return;
        }
        try
        {
            await Task.Delay(200, cts.Token); // pieni viive kirjoituksen ajaksi
            try { await Task.WhenAll(_session.EnsureVodAsync(), _session.EnsureSeriesAsync()); } catch { }
            var live = _session.LiveChannels;
            var vod = _session.Vod;
            var ser = _session.Series;
            // Suodatus taustasäikeessä – listat voivat olla kymmeniä tuhansia
            var result = await Task.Run(() => (
                live.Where(c => c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(MaxPerGroup).ToList(),
                vod.Where(v => v.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(MaxPerGroup).ToList(),
                ser.Where(s => s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(MaxPerGroup).ToList()), cts.Token);
            if (cts.IsCancellationRequested) return;
            (Channels, Movies, Series) = result;
            var total = Channels.Count + Movies.Count + Series.Count;
            Status = total == 0 ? $"Ei tuloksia haulla \"{q}\"." : "";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void PlayLive(LiveChannel? c)
    {
        if (c is null || _main.LiveTv is null) return;
        _main.Navigate(Section.LiveTv);
        _main.LiveTv.SelectedChannel = c;
    }

    [RelayCommand] private void OpenMovie(VodItem? v) { if (v is not null) _main.OpenMovie(v); }
    [RelayCommand] private void OpenSeries(SeriesItem? s) { if (s is not null) _main.OpenSeries(s); }
}
