using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kanava.Controls;
using Kanava.ViewModels;

namespace Kanava.Views;

public partial class ChannelManagerView : UserControl
{
    public ChannelManagerView()
    {
        InitializeComponent();

        // Raahaus yhdelle riville kerrallaan (useat valitut siirretään painikkeilla)
        ReorderBehavior.Attach(ChannelList,
            item => Vm is { CanMoveChannels: true } && item is ManagedChannel,
            (item, before) => Vm?.MoveChannelBefore((ManagedChannel)item, before as ManagedChannel),
            insertBefore: true);
        ReorderBehavior.Attach(CategoryList,
            item => item is ManagedCategory { IsSpecial: false },
            (item, before) => Vm?.MoveCategoryBefore((ManagedCategory)item, before as ManagedCategory),
            insertBefore: true);

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ChannelManagerViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is ChannelManagerViewModel vm) vm.PropertyChanged += OnVmChanged;
        };

        Loaded += (_, _) =>
        {
            // Avautuu valmiiksi valitulla kategorialla
            if (Vm?.SelectedCategory is { } c) CategoryList.SelectedItem = c;
            UpdateCounts();
        };
    }

    private ChannelManagerViewModel? Vm => DataContext as ChannelManagerViewModel;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelManagerViewModel.Channels))
            Dispatcher.BeginInvoke(UpdateCounts, System.Windows.Threading.DispatcherPriority.Background);
        else if (e.PropertyName == nameof(ChannelManagerViewModel.SelectedCategory) && Vm?.SelectedCategory is { } c
                 && !CategoryList.SelectedItems.Contains(c))
            CategoryList.SelectedItem = c;
    }

    private bool _bulkSelect;

    /// <summary>Valitsee kaikki oikeat kategoriat; kanavalista pysyy nykyisessä kategoriassa.</summary>
    private void OnSelectAllCategories(object sender, RoutedEventArgs e)
    {
        _bulkSelect = true;
        try
        {
            CategoryList.UnselectAll();
            foreach (var c in CategoryList.Items.OfType<ManagedCategory>().Where(c => !c.IsSpecial))
                CategoryList.SelectedItems.Add(c);
        }
        finally { _bulkSelect = false; }
        UpdateCounts();
        CategoryList.Focus();
    }

    private void OnSelectNoCategories(object sender, RoutedEventArgs e)
    {
        _bulkSelect = true;
        try { CategoryList.SelectedItem = Vm?.SelectedCategory; }
        finally { _bulkSelect = false; }
        UpdateCounts();
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bulkSelect) return;
        // Kanavalista näyttää viimeksi napsautetun kategorian (myös monivalinnassa)
        if (Vm is { } vm && e.AddedItems.Count > 0 && e.AddedItems[^1] is ManagedCategory c && vm.SelectedCategory != c)
            vm.SelectedCategory = c;
        UpdateCounts();
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCounts();

    private void UpdateCounts()
    {
        CategoryCount.Text = CategoryList.SelectedItems.Count > 1 ? $"{CategoryList.SelectedItems.Count} valittu" : "";
        ChannelCount.Text = ChannelList.SelectedItems.Count > 0
            ? $"{ChannelList.SelectedItems.Count} / {ChannelList.Items.Count} valittu"
            : $"{ChannelList.Items.Count} kanavaa";
    }

    private void OnSelectAllChannels(object sender, RoutedEventArgs e) => ChannelList.SelectAll();
    private void OnSelectNoChannels(object sender, RoutedEventArgs e) => ChannelList.UnselectAll();

    private void OnChannelKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var sel = ChannelList.SelectedItems;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete) { vm.HideChannelsCommand.Execute(sel); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up && vm.CanMoveChannels) { vm.MoveChannelsUpCommand.Execute(sel); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Down && vm.CanMoveChannels) { vm.MoveChannelsDownCommand.Execute(sel); e.Handled = true; }
    }

    private void OnCategoryKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var sel = CategoryList.SelectedItems;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete) { vm.HideCategoriesCommand.Execute(sel); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up) { vm.MoveCategoriesUpCommand.Execute(sel); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Down) { vm.MoveCategoriesDownCommand.Execute(sel); e.Handled = true; }
    }
}
