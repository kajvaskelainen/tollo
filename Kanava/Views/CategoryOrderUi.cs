using System.Windows.Controls;
using Kanava.Controls;
using Kanava.Models;
using Kanava.ViewModels;

namespace Kanava.Views;

/// <summary>
/// Kategorialistan järjestely: raahaus aina päällä (klikkaus valitsee normaalisti)
/// sekä oikean painikkeen valikko. Käytössä Live TV:ssä, Elokuvissa ja Sarjoissa.
/// </summary>
public static class CategoryOrderUi
{
    public static void Attach(ListBox list, Func<CategoryOrderer?> orderer)
    {
        ReorderBehavior.Attach(list,
            item => orderer() is not null && CategoryOrderer.CanMove(item),
            (item, before) => orderer()?.Move((Category)item, before as Category),
            insertBefore: true);

        list.ContextMenu ??= new ContextMenu();
        list.ContextMenuOpening += (_, e) =>
        {
            var menu = list.ContextMenu!;
            menu.Items.Clear();
            var o = orderer();
            if (o is null || ReorderBehavior.FindParent<ListBoxItem>(e.OriginalSource as System.Windows.DependencyObject)?.DataContext is not Category cat)
            {
                e.Handled = true;
                return;
            }
            var movable = CategoryOrderer.CanMove(cat);
            menu.Items.Add(Item("Siirrä alkuun", "", () => o.MoveToTop(cat), movable));
            menu.Items.Add(Item("Siirrä loppuun", "", () => o.MoveToBottom(cat), movable));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Palauta kategorioiden oletusjärjestys", "", o.Reset));
        };
    }

    private static MenuItem Item(string header, string icon, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Tag = icon, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }
}
