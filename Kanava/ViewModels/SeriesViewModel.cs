using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class SeriesViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _detailsCts;

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private IReadOnlyList<SeriesItem> _items = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private SeriesItem? _selected;
    [ObservableProperty] private SeriesDetails? _details;
    [ObservableProperty] private bool _isDetailsLoading;
    [ObservableProperty] private Season? _selectedSeason;

    public SeriesViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _session.UserData.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnUserDataChanged);
        _ = LoadAsync();
    }

    // ---------- Jatka katsomista / katsotut ----------

    private void OnUserDataChanged()
    {
        if (Details is { } d) ApplyEpisodeState(d);
        if (SelectedCategory?.Special == SpecialCategory.Continue) ApplyFilter();
        OnPropertyChanged(nameof(ContinueText));
    }

    private void ApplyEpisodeState(SeriesDetails d)
    {
        var ud = _session.UserData;
        foreach (var ep in d.Seasons.SelectMany(s => s.Episodes))
        {
            ep.Progress = ud.GetProgress(ep.ProgressKey)?.Fraction ?? 0;
            ep.IsWatched = ud.IsWatched(ep.ProgressKey);
        }
    }

    private List<Episode> AllEpisodes => Details?.Seasons.SelectMany(s => s.Episodes).ToList() ?? [];

    /// <summary>Mistä jatketaan: viimeksi kesken jäänyt jakso, tai viimeisen katsotun jälkeinen.</summary>
    private (Episode Ep, long ResumeMs)? FindContinue()
    {
        if (Selected is not { } series) return null;
        var all = AllEpisodes;
        if (all.Count == 0) return null;
        var ud = _session.UserData;
        foreach (var (key, p) in ud.InProgress())
        {
            if (p.SeriesId != series.SeriesId) continue;
            if (all.FirstOrDefault(e => e.ProgressKey == key) is { } ep) return (ep, p.PositionMs);
        }
        var last = all.FindLastIndex(e => e.IsWatched);
        return last >= 0 && last + 1 < all.Count ? (all[last + 1], 0) : null;
    }

    public string? ContinueText => FindContinue() is { } c
        ? (c.ResumeMs > 0 ? $"Jatka: K{c.Ep.Season} J{c.Ep.Number} ({MoviesViewModel.TimeText(c.ResumeMs)})" : $"Seuraava: K{c.Ep.Season} J{c.Ep.Number}")
        : null;

    [RelayCommand]
    private void ContinueSeries()
    {
        if (FindContinue() is { } c) StartEpisode(c.Ep, c.ResumeMs);
    }

    [RelayCommand]
    private void ToggleEpisodeWatched(Episode? ep)
    {
        if (ep is null) return;
        _session.UserData.SetWatched(ep.ProgressKey, !ep.IsWatched);
    }

    [RelayCommand]
    private void MarkSeasonWatched()
    {
        if (SelectedSeason is not { } season) return;
        var all = season.Episodes.All(e => e.IsWatched);
        foreach (var ep in season.Episodes) _session.UserData.SetWatched(ep.ProgressKey, !all);
    }

    private PlaybackRequest BuildRequest(Episode ep, long resumeMs, SeriesItem series, List<Episode> all)
    {
        var idx = all.IndexOf(ep);
        var next = idx >= 0 && idx + 1 < all.Count ? all[idx + 1] : null;
        return new PlaybackRequest
        {
            Url = _session.Client.EpisodeUrl(ep.Id, ep.ContainerExtension),
            Title = $"{series.Name} – K{ep.Season} J{ep.Number} {ep.Title}",
            ProgressKey = ep.ProgressKey,
            SyncKey = $"series:{series.SeriesId}",
            ResumeMs = resumeMs,
            SeriesId = series.SeriesId,
            Season = ep.Season,
            Episode = ep.Number,
            NextLabel = next is null ? null : $"K{next.Season} J{next.Number} {next.Title}",
            Next = next is null ? null : () =>
            {
                var p = _session.UserData.GetProgress(next.ProgressKey);
                return BuildRequest(next, p?.PositionMs ?? 0, series, all);
            },
        };
    }

    private void StartEpisode(Episode ep, long resumeMs)
    {
        if (Selected is not { } series) return;
        _main.PlayVod(BuildRequest(ep, resumeMs, series, AllEpisodes));
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            await _session.EnsureSeriesAsync();
            Categories.Clear();
            Categories.Add(new Category { Id = "__continue", Name = "Jatka katsomista", Special = SpecialCategory.Continue });
            Categories.Add(new Category { Id = "__all", Name = "Kaikki sarjat", Special = SpecialCategory.All });
            Categories.Add(new Category { Id = "__fav", Name = "Suosikit", Special = SpecialCategory.Favorites });
            foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
            SelectedCategory ??= Categories[1];
            ApplyFilter();
        }
        catch (Exception ex) { Error = "Sarjojen lataus epäonnistui: " + ex.Message; }
        finally { IsLoading = false; }
    }

    private CategoryOrderer? _categoryOrder;
    public CategoryOrderer CategoryOrder => _categoryOrder ??= new(Categories, _session.UserData, "series", () => _session.SeriesCategories)
    {
        GetSelected = () => SelectedCategory,
        SetSelected = c => SelectedCategory = c,
    };

    [RelayCommand] private Task Retry() => LoadAsync();

    partial void OnSelectedCategoryChanged(Category? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<SeriesItem> q = _session.Series;
        var cat = SelectedCategory;
        if (cat?.Special == SpecialCategory.Continue)
        {
            var order = new Dictionary<int, int>();
            foreach (var (_, p) in _session.UserData.InProgress())
                if (p.SeriesId is { } id) order.TryAdd(id, order.Count);
            q = q.Where(v => order.ContainsKey(v.SeriesId)).OrderBy(v => order[v.SeriesId]);
        }
        else if (cat?.Special == SpecialCategory.Favorites) q = q.Where(v => v.IsFavorite);
        else if (cat is { Special: SpecialCategory.None }) q = q.Where(v => v.CategoryId == cat.Id);
        var s = SearchText.Trim();
        if (s.Length > 0) q = q.Where(v => v.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Items = q.ToList();
    }

    partial void OnSelectedChanged(SeriesItem? value)
    {
        OnPropertyChanged(nameof(ContinueText));
        Details = null;
        SelectedSeason = null;
        _detailsCts?.Cancel();
        if (value is not null) _ = LoadDetailsAsync(value);
    }

    private async Task LoadDetailsAsync(SeriesItem item)
    {
        var cts = _detailsCts = new CancellationTokenSource();
        IsDetailsLoading = true;
        try
        {
            var d = await _session.Client.GetSeriesInfoAsync(item.SeriesId, cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyEpisodeState(d);
            Details = d;
            // Avataan kausi, josta jatketaan
            var cont = FindContinue();
            SelectedSeason = cont is { } c ? d.Seasons.FirstOrDefault(s => s.Episodes.Contains(c.Ep)) ?? d.Seasons.FirstOrDefault() : d.Seasons.FirstOrDefault();
            OnPropertyChanged(nameof(ContinueText));
        }
        catch { if (!cts.IsCancellationRequested) Details = new SeriesDetails { Plot = "Jaksotietoja ei saatu ladattua." }; }
        finally { if (_detailsCts == cts) IsDetailsLoading = false; }
    }

    public async void Open(SeriesItem item)
    {
        await _session.EnsureSeriesAsync();
        if (!string.IsNullOrEmpty(SearchText)) SearchText = "";
        Selected = item;
    }

    [RelayCommand]
    private void CloseDetails() => Selected = null;

    [RelayCommand]
    private void PlayEpisode(Episode? ep)
    {
        if (ep is null) return;
        var resume = ep.IsWatched ? 0 : _session.UserData.GetProgress(ep.ProgressKey)?.PositionMs ?? 0;
        StartEpisode(ep, resume);
    }

    [RelayCommand]
    private void ToggleFavorite(SeriesItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        _session.UserData.ToggleSeriesFavorite(item);
        if (SelectedCategory?.Special == SpecialCategory.Favorites) ApplyFilter();
    }
}
