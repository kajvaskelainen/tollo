using System.Windows.Controls;
using System.Windows.Input;

namespace Kanava.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboard.Focus(QueryBox);
    }
}
