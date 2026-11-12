using System.Windows.Controls;
using Kanava.ViewModels;

namespace Kanava.Views;

public partial class SeriesView : UserControl
{
    public SeriesView()
    {
        InitializeComponent();
        CategoryOrderUi.Attach(CategoryList, () => (DataContext as SeriesViewModel)?.CategoryOrder);
    }
}
