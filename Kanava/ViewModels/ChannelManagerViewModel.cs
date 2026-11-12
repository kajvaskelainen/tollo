using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>Kategoriarivi kanavien hallinnassa.</summary>
public partial class ManagedCategory : ObservableObject
{
    public required string Key { get; init; }          // "__all", "__fav" tai kategorian id
    public required string Name { get; init; }
    public bool IsSpecial { get; init; }
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _hiddenCount;

    public string CountText => HiddenCount > 0 ? $"{Count} · {HiddenCount} piilossa" : Count.ToString();
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
    partial void OnHiddenCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}

/// <summary>Kanavarivi kanavien hallinnassa.</summary>
public partial class ManagedChannel : ObservableObject
{
    public required LiveChannel Channel { get; init; }
    public string CategoryName { get; init; } = "";
    /// <summary>Kanava on itse piilotettu.</summary>
    [ObservableProperty] private bool _isHidden;
    /// <summary>Kanavan kategoria on piilotettu (kanava ei näy, vaikkei sitä ole itse piilotettu).</summary>
    [ObservableProperty] private bool _isCategoryHidden;

    public bool IsEffectivelyHidden => IsHidden || IsCategoryHidden;
    partial void OnIsHiddenChanged(bool value) => OnPropertyChanged(nameof(IsEffectivelyHidden));
    partial void OnIsCategoryHiddenChanged(bool value) => OnPropertyChanged(nameof(IsEffectivelyHidden));
}

/// <summary>
/// Kanavien hallinta: piilotus, monivalinta, järjestys ja kanavalistan uudelleenlataus.
/// Komennot saavat parametrina listan valituista riveistä (ListBox.SelectedItems).
/// </summary>
public partial class ChannelManagerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;

    public ObservableCollection<ManagedCategory> Categories { get; } = [];
    /// <summary>Vaihdetaan kokonaan, kun kategoria/haku muuttuu (tuhansien rivien lisäys yksitellen olisi hidasta).</summary>
    [ObservableProperty] private ObservableCollection<ManagedChannel> _channels = [];

    [ObservableProperty] private ManagedCategory? _selectedCategory;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isReloading;

    /// <summary>Hallinnassa on tehty muutoksia → Live TV rakennetaan uudelleen palattaessa.</summary>
    public bool IsDirty { get; private set; }

    public ChannelManagerViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        BuildCategories();
        SelectedCategory = Categories.FirstOrDefault();
    }

    private UserDataStore Ud => _session.UserData;

    public string Summary
    {
        get
        {
            var total = _session.LiveChannels.Count;
            var visible = _session.LiveChannels.Count(_session.IsVisible);
            var hiddenCats = _session.LiveCategories.Count(c => Ud.IsLiveCategoryHidden(c.Id));
            var s = $"{total} kanavaa · {visible} näkyvissä · {total - visible} piilossa";
            return hiddenCats > 0 ? s + $" · {hiddenCats} kategoriaa piilossa" : s;
        }
    }

    /// <summary>Kategoriasarake näytetään koostelistoissa (Kaikki, Suosikit).</summary>
    public bool ShowCategoryColumn => SelectedCategory?.IsSpecial == true;

    /// <summary>Järjestys on muutettavissa (ei haun aikana: suodatettu lista ei kerro paikkoja).</summary>
    public bool CanMoveChannels => SearchText.Trim().Length == 0 && SelectedCategory is not null;

    // ---------- Rakennus ----------

    private void BuildCategories()
    {
        var selectedKey = SelectedCategory?.Key;
        Categories.Clear();
        Categories.Add(new ManagedCategory { Key = "__all", Name = "Kaikki kanavat", IsSpecial = true });
        Categories.Add(new ManagedCategory { Key = "__fav", Name = "Suosikit", IsSpecial = true });
        foreach (var c in _session.OrderedLiveCategories())
            Categories.Add(new ManagedCategory { Key = c.Id, Name = c.Name, IsHidden = Ud.IsLiveCategoryHidden(c.Id) });
        UpdateCounts();
        if (selectedKey is not null) SelectedCategory = Categories.FirstOrDefault(c => c.Key == selectedKey) ?? Categories[0];
    }

    private void UpdateCounts()
    {
        var byCat = _session.LiveChannels.GroupBy(c => c.CategoryId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var mc in Categories)
        {
            var list = mc.Key switch
            {
                "__all" => _session.LiveChannels,
                "__fav" => _session.LiveChannels.Where(c => c.IsFavorite).ToList(),
                _ => byCat.GetValueOrDefault(mc.Key) ?? [],
            };
            mc.Count = list.Count;
            mc.HiddenCount = list.Count(c => !_session.IsVisible(c));
        }
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnSelectedCategoryChanged(ManagedCategory? value) => BuildChannels();

    partial void OnSearchTextChanged(string value)
    {
        BuildChannels();
        OnPropertyChanged(nameof(CanMoveChannels));
    }

    private void BuildChannels()
    {
        if (SelectedCategory is not { } cat) { Channels = []; return; }
        var names = _session.LiveCategories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        IEnumerable<LiveChannel> list = _session.OrderedChannels(cat.Key, includeHidden: true);
        var s = SearchText.Trim();
        if (s.Length > 0) list = list.Where(c => c.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Channels = new ObservableCollection<ManagedChannel>(list.Select(c => new ManagedChannel
        {
            Channel = c,
            CategoryName = names.GetValueOrDefault(c.CategoryId, ""),
            IsHidden = Ud.IsLiveHidden(c.StreamId),
            IsCategoryHidden = Ud.IsLiveCategoryHidden(c.CategoryId),
        }));
        OnPropertyChanged(nameof(CanMoveChannels));
        OnPropertyChanged(nameof(ShowCategoryColumn));
    }

    private void Changed(string status)
    {
        IsDirty = true;
        Status = status;
    }

    private static List<T> Selected<T>(object? items) => items is IList l ? l.OfType<T>().ToList() : [];

    // ---------- Kanavat: piilotus ja suosikit ----------

    [RelayCommand]
    private void HideChannels(object? items) => SetChannelsHidden(Selected<ManagedChannel>(items), true);

    [RelayCommand]
    private void ShowChannels(object? items) => SetChannelsHidden(Selected<ManagedChannel>(items), false);

    private void SetChannelsHidden(List<ManagedChannel> rows, bool hidden)
    {
        if (rows.Count == 0) return;
        Ud.SetLiveHidden(rows.Select(r => r.Channel.StreamId), hidden);
        foreach (var r in rows) r.IsHidden = hidden;
        UpdateCounts();
        var stillHiddenByCat = !hidden ? rows.Count(r => r.IsCategoryHidden) : 0;
        Changed((hidden ? $"Piilotettu {rows.Count} kanavaa." : $"Näytetään {rows.Count} kanavaa.")
                + (stillHiddenByCat > 0 ? $" Huom: {stillHiddenByCat} on piilotetussa kategoriassa." : ""));
    }

    [RelayCommand]
    private void AddFavorites(object? items)
    {
        var rows = Selected<ManagedChannel>(items).Where(r => !r.Channel.IsFavorite).ToList();
        foreach (var r in rows) Ud.ToggleLiveFavorite(r.Channel);
        UpdateCounts();
        Changed($"Lisätty {rows.Count} kanavaa suosikkeihin.");
    }

    [RelayCommand]
    private void RemoveFavorites(object? items)
    {
        var rows = Selected<ManagedChannel>(items).Where(r => r.Channel.IsFavorite).ToList();
        foreach (var r in rows) Ud.ToggleLiveFavorite(r.Channel);
        if (SelectedCategory?.Key == "__fav") foreach (var r in rows) Channels.Remove(r);
        UpdateCounts();
        Changed($"Poistettu {rows.Count} kanavaa suosikeista.");
    }

    // ---------- Kanavat: järjestys ----------

    [RelayCommand] private void MoveChannelsUp(object? items) => MoveBy(Channels, Selected<ManagedChannel>(items), -1, SaveChannels);
    [RelayCommand] private void MoveChannelsDown(object? items) => MoveBy(Channels, Selected<ManagedChannel>(items), +1, SaveChannels);
    [RelayCommand] private void MoveChannelsTop(object? items) => MoveToEdge(Channels, Selected<ManagedChannel>(items), top: true, 0, SaveChannels);
    [RelayCommand] private void MoveChannelsBottom(object? items) => MoveToEdge(Channels, Selected<ManagedChannel>(items), top: false, 0, SaveChannels);

    /// <summary>Raahaus: siirtää rivin kohteen <paramref name="before"/> eteen (null = loppuun).</summary>
    public void MoveChannelBefore(ManagedChannel item, ManagedChannel? before)
    {
        if (!CanMoveChannels) return;
        if (MoveBefore(Channels, item, before, 0)) SaveChannels();
    }

    [RelayCommand]
    private void ResetChannelOrder()
    {
        if (SelectedCategory is not { } cat) return;
        if (cat.Key == "__fav")
            Ud.SetChannelOrder("__fav", _session.LiveChannels.Where(c => c.IsFavorite).Select(c => c.StreamId));
        else Ud.SetChannelOrder(cat.Key, null);
        BuildChannels();
        Changed($"{cat.Name}: palvelimen järjestys palautettu.");
    }

    private void SaveChannels()
    {
        if (!CanMoveChannels || SelectedCategory is not { } cat) return;
        // Hallinnan lista sisältää myös piilotetut, joten se on koko järjestys
        Ud.SetChannelOrder(cat.Key, Channels.Select(r => r.Channel.StreamId).ToList());
        Changed("Järjestys tallennettu.");
    }

    // ---------- Kategoriat ----------

    [RelayCommand]
    private void HideCategories(object? items) => SetCategoriesHidden(Selected<ManagedCategory>(items), true);

    [RelayCommand]
    private void ShowCategories(object? items) => SetCategoriesHidden(Selected<ManagedCategory>(items), false);

    private void SetCategoriesHidden(List<ManagedCategory> rows, bool hidden)
    {
        rows = rows.Where(r => !r.IsSpecial).ToList();
        if (rows.Count == 0) return;
        Ud.SetLiveCategoriesHidden(rows.Select(r => r.Key), hidden);
        foreach (var r in rows) r.IsHidden = hidden;
        var keys = rows.Select(r => r.Key).ToHashSet();
        foreach (var ch in Channels.Where(c => keys.Contains(c.Channel.CategoryId))) ch.IsCategoryHidden = hidden;
        UpdateCounts();
        Changed(hidden ? $"Piilotettu {rows.Count} kategoriaa kanavineen." : $"Näytetään {rows.Count} kategoriaa.");
    }

    private int FirstMovableCategory => Categories.Count(c => c.IsSpecial);

    [RelayCommand] private void MoveCategoriesUp(object? items) => MoveBy(Categories, MovableCats(items), -1, SaveCategories, FirstMovableCategory);
    [RelayCommand] private void MoveCategoriesDown(object? items) => MoveBy(Categories, MovableCats(items), +1, SaveCategories, FirstMovableCategory);
    [RelayCommand] private void MoveCategoriesTop(object? items) => MoveToEdge(Categories, MovableCats(items), top: true, FirstMovableCategory, SaveCategories);
    [RelayCommand] private void MoveCategoriesBottom(object? items) => MoveToEdge(Categories, MovableCats(items), top: false, FirstMovableCategory, SaveCategories);

    private static List<ManagedCategory> MovableCats(object? items) => Selected<ManagedCategory>(items).Where(c => !c.IsSpecial).ToList();

    public void MoveCategoryBefore(ManagedCategory item, ManagedCategory? before)
    {
        if (item.IsSpecial) return;
        if (MoveBefore(Categories, item, before, FirstMovableCategory)) SaveCategories();
    }

    [RelayCommand]
    private void ResetCategoryOrder()
    {
        Ud.SetCategoryOrder("live", null);
        BuildCategories();
        Changed("Kategorioiden palvelimen järjestys palautettu.");
    }

    private void SaveCategories()
    {
        Ud.SetCategoryOrder("live", Categories.Where(c => !c.IsSpecial).Select(c => c.Key).ToList());
        Changed("Kategorioiden järjestys tallennettu.");
    }

    // ---------- Yleiset siirrot (säilyttävät valinnan, koska rivit siirretään Move-kutsuilla) ----------

    /// <summary>Siirtää valittuja yhden askeleen; valitut "ryhmät" liikkuvat yhdessä eivätkä ohita toisiaan.</summary>
    private static void MoveBy<T>(ObservableCollection<T> list, List<T> selected, int dir, Action save, int min = 0)
    {
        if (selected.Count == 0) return;
        var set = selected.ToHashSet();
        var moved = false;
        if (dir < 0)
        {
            for (var i = min + 1; i < list.Count; i++)
                if (set.Contains(list[i]) && !set.Contains(list[i - 1])) { list.Move(i, i - 1); moved = true; }
        }
        else
        {
            for (var i = list.Count - 2; i >= min; i--)
                if (set.Contains(list[i]) && !set.Contains(list[i + 1])) { list.Move(i, i + 1); moved = true; }
        }
        if (moved) save();
    }

    private static void MoveToEdge<T>(ObservableCollection<T> list, List<T> selected, bool top, int min, Action save)
    {
        if (selected.Count == 0) return;
        var set = selected.ToHashSet();
        var ordered = list.Where(set.Contains).ToList(); // nykyinen keskinäinen järjestys
        if (top)
            for (var k = 0; k < ordered.Count; k++) list.Move(list.IndexOf(ordered[k]), min + k);
        else
            for (var k = 0; k < ordered.Count; k++) list.Move(list.IndexOf(ordered[k]), list.Count - 1);
        save();
    }

    private static bool MoveBefore<T>(ObservableCollection<T> list, T item, T? before, int min) where T : class
    {
        var from = list.IndexOf(item);
        if (from < 0) return false;
        var to = before is null ? list.Count : list.IndexOf(before);
        if (to < 0) return false;
        if (to > from) to--;
        to = Math.Clamp(to, min, list.Count - 1);
        if (to == from) return false;
        list.Move(from, to);
        return true;
    }

    // ---------- Kanavalistan uudelleenlataus ----------

    [RelayCommand]
    private async Task ReloadChannels()
    {
        if (IsReloading) return;
        IsReloading = true;
        Status = "Ladataan kanavalistaa palvelimelta…";
        try
        {
            var summary = await _session.ReloadLiveAsync();
            BuildCategories();
            BuildChannels();
            Changed(summary);
        }
        catch (Exception ex) { Status = "Lataus epäonnistui: " + ex.Message; }
        finally { IsReloading = false; }
    }

    [RelayCommand]
    private void Back() => _main.Navigate(Section.LiveTv);
}
