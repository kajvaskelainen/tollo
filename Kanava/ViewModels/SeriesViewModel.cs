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
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            await _session.EnsureSeriesAsync();
            Categories.Clear();
            Categories.Add(new Category { Id = "__all", Name = "Kaikki sarjat", Special = SpecialCategory.All });
            Categories.Add(new Category { Id = "__fav", Name = "Suosikit", Special = SpecialCategory.Favorites });
            foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
            SelectedCategory ??= Categories[0];
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
        if (cat?.Special == SpecialCategory.Favorites) q = q.Where(v => v.IsFavorite);
        else if (cat is { Special: SpecialCategory.None }) q = q.Where(v => v.CategoryId == cat.Id);
        var s = SearchText.Trim();
        if (s.Length > 0) q = q.Where(v => v.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Items = q.ToList();
    }

    partial void OnSelectedChanged(SeriesItem? value)
    {
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
            Details = d;
            SelectedSeason = d.Seasons.FirstOrDefault();
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
        if (ep is null || Selected is null) return;
        _main.PlayVod(_session.Client.EpisodeUrl(ep.Id, ep.ContainerExtension),
            $"{Selected.Name} – K{ep.Season} J{ep.Number} {ep.Title}");
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
