using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kanava.Models;
using Kanava.ViewModels;

namespace Kanava.Views;

public partial class MoviesView : UserControl
{
    public MoviesView()
    {
        InitializeComponent();
        CategoryOrderUi.Attach(CategoryList, () => (DataContext as MoviesViewModel)?.CategoryOrder);
    }

    /// <summary>Tuplaklikkaus julisteeseen = toista heti.</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (LiveTvView.FindParent<Button>(src) is not null) return;
        if (LiveTvView.FindParent<ListBoxItem>(src)?.DataContext is VodItem item && DataContext is MoviesViewModel vm)
            vm.PlayCommand.Execute(item);
    }
}
