using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class MoviesViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _detailsCts;

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private IReadOnlyList<VodItem> _items = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private VodItem? _selected;
    [ObservableProperty] private VodDetails? _details;
    [ObservableProperty] private bool _isDetailsLoading;

    public MoviesViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _session.UserData.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnUserDataChanged);
        _ = LoadAsync();
    }

    // ---------- Jatka katsomista / katsotut ----------

    private void OnUserDataChanged()
    {
        RefreshProgress();
        if (SelectedCategory?.Special == SpecialCategory.Continue) ApplyFilter();
        OnPropertyChanged(nameof(ResumeText));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(WatchedText));
    }

    private void RefreshProgress()
    {
        var ud = _session.UserData;
        foreach (var v in _session.Vod)
        {
            var p = ud.GetProgress(v.ProgressKey);
            v.Progress = p?.Fraction ?? 0;
            v.IsWatched = ud.IsWatched(v.ProgressKey);
        }
    }

    public bool HasProgress => Selected is { } s && _session.UserData.GetProgress(s.ProgressKey) is not null;
    public string ResumeText => Selected is { } s && _session.UserData.GetProgress(s.ProgressKey) is { } p
        ? $"Jatka ({TimeText(p.PositionMs)})" : "Toista";
    public string WatchedText => Selected is { } s && _session.UserData.IsWatched(s.ProgressKey) ? "Merkitse katsomattomaksi" : "Merkitse katsotuksi";

    public static string TimeText(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    [RelayCommand]
    private void ToggleWatched()
    {
        if (Selected is not { } s) return;
        _session.UserData.SetWatched(s.ProgressKey, !_session.UserData.IsWatched(s.ProgressKey));
    }

    [RelayCommand]
    private void PlayFromStart() => StartPlayback(Selected, fromStart: true);

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            await _session.EnsureVodAsync();
            Categories.Clear();
            Categories.Add(new Category { Id = "__continue", Name = "Jatka katsomista", Special = SpecialCategory.Continue });
            Categories.Add(new Category { Id = "__all", Name = "Kaikki elokuvat", Special = SpecialCategory.All });
            Categories.Add(new Category { Id = "__fav", Name = "Suosikit", Special = SpecialCategory.Favorites });
            foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
            RefreshProgress();
            SelectedCategory ??= Categories[1];
            ApplyFilter();
        }
        catch (Exception ex) { Error = "Elokuvien lataus epäonnistui: " + ex.Message; }
        finally { IsLoading = false; }
    }

    private CategoryOrderer? _categoryOrder;
    public CategoryOrderer CategoryOrder => _categoryOrder ??= new(Categories, _session.UserData, "vod", () => _session.VodCategories)
    {
        GetSelected = () => SelectedCategory,
        SetSelected = c => SelectedCategory = c,
    };

    [RelayCommand] private Task Retry() => LoadAsync();

    partial void OnSelectedCategoryChanged(Category? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<VodItem> q = _session.Vod;
        var cat = SelectedCategory;
        if (cat?.Special == SpecialCategory.Continue)
        {
            var order = _session.UserData.InProgress().Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
            q = q.Where(v => order.ContainsKey(v.ProgressKey)).OrderBy(v => order[v.ProgressKey]);
        }
        else if (cat?.Special == SpecialCategory.Favorites) q = q.Where(v => v.IsFavorite);
        else if (cat is { Special: SpecialCategory.None }) q = q.Where(v => v.CategoryId == cat.Id);
        var s = SearchText.Trim();
        if (s.Length > 0) q = q.Where(v => v.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Items = q.ToList();
    }

    partial void OnSelectedChanged(VodItem? value)
    {
        OnPropertyChanged(nameof(ResumeText));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(WatchedText));
        Details = null;
        _detailsCts?.Cancel();
        if (value is not null) _ = LoadDetailsAsync(value);
    }

    private async Task LoadDetailsAsync(VodItem item)
    {
        var cts = _detailsCts = new CancellationTokenSource();
        IsDetailsLoading = true;
        try
        {
            var d = await _session.Client.GetVodInfoAsync(item.StreamId, cts.Token);
            if (!cts.IsCancellationRequested) Details = d;
        }
        catch { if (!cts.IsCancellationRequested) Details = new VodDetails { Plot = "Lisätietoja ei saatu ladattua." }; }
        finally { if (_detailsCts == cts) IsDetailsLoading = false; }
    }

    /// <summary>Avaa elokuvan tiedot (esim. hausta tai suosikeista).</summary>
    public async void Open(VodItem item)
    {
        await _session.EnsureVodAsync();
        if (!string.IsNullOrEmpty(SearchText)) SearchText = "";
        Selected = item;
    }

    [RelayCommand]
    private void CloseDetails() => Selected = null;

    /// <summary>Toista – jatkaa tallennetusta kohdasta, jos elokuva on kesken.</summary>
    [RelayCommand]
    private void Play(VodItem? item) => StartPlayback(item ?? Selected, fromStart: false);

    private void StartPlayback(VodItem? item, bool fromStart)
    {
        if (item is null) return;
        var ext = !string.IsNullOrEmpty(Details?.ContainerExtension) && Selected == item ? Details!.ContainerExtension : item.ContainerExtension;
        var resume = fromStart ? 0 : _session.UserData.GetProgress(item.ProgressKey)?.PositionMs ?? 0;
        _main.PlayVod(new PlaybackRequest
        {
            Url = _session.Client.MovieUrl(item.StreamId, ext),
            Title = item.Name,
            ProgressKey = item.ProgressKey,
            ResumeMs = resume,
        });
    }

    [RelayCommand]
    private void ToggleFavorite(VodItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        _session.UserData.ToggleVodFavorite(item);
        if (SelectedCategory?.Special == SpecialCategory.Favorites) ApplyFilter();
    }
}
