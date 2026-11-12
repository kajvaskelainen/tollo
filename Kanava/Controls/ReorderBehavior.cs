using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Kanava.Controls;

/// <summary>
/// Raahaa ja pudota -järjestely ListBoxille. Pudotuskohde merkitään liitetyllä
/// IsDropTarget-ominaisuudella, jota kohteiden tyyli voi käyttää korostukseen.
/// </summary>
public static class ReorderBehavior
{
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(ReorderBehavior), new PropertyMetadata(false));

    public static bool GetIsDropTarget(DependencyObject d) => (bool)d.GetValue(IsDropTargetProperty);
    public static void SetIsDropTarget(DependencyObject d, bool v) => d.SetValue(IsDropTargetProperty, v);

    private const string Format = "Kanava.ReorderItem";

    /// <param name="list">Järjesteltävä lista.</param>
    /// <param name="canReorder">Onko raahaus sallittu juuri nyt.</param>
    /// <param name="move">Kutsutaan pudotuksessa: (raahattu kohde, kohde jonka paikalle).
    /// Pystylistassa (<paramref name="insertBefore"/>) toinen parametri on kohde, jonka eteen lisätään (null = loppuun).</param>
    /// <param name="insertBefore">Pystylista: pudotus kohteen ylä- tai alapuolelle hiiren mukaan.</param>
    public static void Attach(ListBox list, Func<object, bool> canReorder, Action<object, object?> move, bool insertBefore = false)
    {
        // Pystylistassa pudotuskohta on se kohde, jonka eteen raahattu kohde menee
        // Palauttaa kohdedatan (null = loppuun) ja korostettavan säiliön
        object? Resolve(DragEventArgs e, out ListBoxItem? highlight)
        {
            var t = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
            highlight = t;
            if (!insertBefore || t is null) return t?.DataContext;
            if (e.GetPosition(t).Y < t.ActualHeight / 2) return t.DataContext;
            var i = list.ItemContainerGenerator.IndexFromContainer(t);
            if (i < 0 || i + 1 >= list.Items.Count) { highlight = null; return null; }
            highlight = list.ItemContainerGenerator.ContainerFromIndex(i + 1) as ListBoxItem;
            return list.Items[i + 1];
        }

        Point? start = null;
        object? dragItem = null;
        ListBoxItem? lastTarget = null;

        void ClearTarget()
        {
            if (lastTarget is not null) SetIsDropTarget(lastTarget, false);
            lastTarget = null;
        }

        list.AllowDrop = true;

        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            dragItem = null;
            var src = e.OriginalSource as DependencyObject;
            if (FindParent<ButtonBase>(src) is not null || FindParent<ScrollBar>(src) is not null) return;
            if (FindParent<ListBoxItem>(src) is { } item && canReorder(item.DataContext))
            {
                start = e.GetPosition(list);
                dragItem = item.DataContext;
            }
        };

        list.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } s || dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(list) - s;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = dragItem;
            start = null;
            dragItem = null;
            DragDrop.DoDragDrop(list, new DataObject(Format, item), DragDropEffects.Move);
            ClearTarget();
        };

        list.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move;
            Resolve(e, out var target);
            if (target != lastTarget)
            {
                ClearTarget();
                if (target is not null && target.DataContext != e.Data.GetData(Format))
                {
                    SetIsDropTarget(target, true);
                    lastTarget = target;
                }
            }
            AutoScroll(list, e.GetPosition(list));
            e.Handled = true;
        };

        list.DragLeave += (_, _) => { };

        list.Drop += (_, e) =>
        {
            var item = e.Data.GetData(Format);
            var target = Resolve(e, out ListBoxItem? _);
            ClearTarget();
            if (item is not null && item != target) move(item, target);
            e.Handled = true;
        };
    }

    /// <summary>Vierittää listaa, kun raahataan reunan lähelle.</summary>
    private static void AutoScroll(ListBox list, Point p)
    {
        if (FindChild<ScrollViewer>(list) is not { } sv) return;
        const double edge = 40;
        if (p.Y < edge) sv.ScrollToVerticalOffset(sv.VerticalOffset - 20);
        else if (p.Y > list.ActualHeight - edge) sv.ScrollToVerticalOffset(sv.VerticalOffset + 20);
    }

    public static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }
}
