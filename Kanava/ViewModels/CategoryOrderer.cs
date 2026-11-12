using System.Collections.ObjectModel;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>
/// Kategorialistan oma järjestys (Live TV, Elokuvat, Sarjat). Erikoiskategoriat
/// (Kaikki, Suosikit, Historia) pysyvät aina ylimpinä; vain palvelimen kategoriat siirtyvät.
/// </summary>
public sealed class CategoryOrderer(ObservableCollection<Category> categories, UserDataStore store, string kind, Func<IEnumerable<Category>> source)
{
    /// <summary>Palvelimen kategoriat tallennetussa järjestyksessä.</summary>
    public List<Category> Ordered() => UserDataStore.ApplyOrder(source(), store.GetCategoryOrder(kind), c => c.Id);

    public static bool CanMove(object? c) => c is Category { Special: SpecialCategory.None };

    private int FirstMovable => categories.Count(c => c.Special != SpecialCategory.None);

    /// <summary>Siirtää kategorian kohteen <paramref name="before"/> eteen (null = loppuun).</summary>
    public void Move(Category item, Category? before)
    {
        if (!CanMove(item)) return;
        var from = categories.IndexOf(item);
        if (from < 0) return;
        var to = before is null ? categories.Count : categories.IndexOf(before);
        if (to < 0) return;
        if (to > from) to--;               // poisto siirtää myöhempiä yhdellä ylös
        to = Math.Clamp(to, FirstMovable, categories.Count - 1);
        if (from == to) return;
        categories.Move(from, to);
        Save();
    }

    public void MoveToTop(Category c)
    {
        if (!CanMove(c)) return;
        categories.Move(categories.IndexOf(c), FirstMovable);
        Save();
    }

    public void MoveToBottom(Category c)
    {
        if (!CanMove(c)) return;
        categories.Move(categories.IndexOf(c), categories.Count - 1);
        Save();
    }

    /// <summary>Valittu kategoria (palautetaan, koska listan tyhjennys nollaa ListBoxin valinnan).</summary>
    public Func<Category?>? GetSelected { get; init; }
    public Action<Category?>? SetSelected { get; init; }

    /// <summary>Palauttaa palvelimen järjestyksen.</summary>
    public void Reset()
    {
        var selected = GetSelected?.Invoke();
        store.SetCategoryOrder(kind, null);
        var specials = categories.Where(c => c.Special != SpecialCategory.None).ToList();
        categories.Clear();
        foreach (var c in specials) categories.Add(c);
        foreach (var c in source()) categories.Add(c);
        SetSelected?.Invoke(selected);
    }

    private void Save() => store.SetCategoryOrder(kind, categories.Where(c => c.Special == SpecialCategory.None).Select(c => c.Id));
}
