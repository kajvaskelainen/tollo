using LibVLCSharp.Shared;

namespace Kanava.Services;

/// <summary>
/// Chromecast-laitteiden haku lähiverkosta (libVLC:n mDNS-renderöijähaku). Haku käynnistetään,
/// kun käyttäjä avaa suoratoistovalikon ensimmäisen kerran.
/// </summary>
public static class CastDiscovery
{
    private static RendererDiscoverer? _discoverer;
    private static readonly List<RendererItem> Items = [];
    private static readonly object Lock = new();

    public static event Action? Changed;

    public static IReadOnlyList<RendererItem> Devices { get { lock (Lock) return Items.ToList(); } }

    public static bool IsSearching => _discoverer is not null;

    public static void Ensure(LibVLC libvlc)
    {
        if (_discoverer is not null) return;
        // Chromecast voi kysyä vahvistusta (esim. uudelleenpakkaus) – hyväksytään automaattisesti
        libvlc.SetDialogHandlers(
            (title, text) => Task.CompletedTask,
            (dialog, title, text, user, store, token) => { dialog.Dismiss(); return Task.CompletedTask; },
            (dialog, title, text, type, cancel, action1, action2, token) => { dialog.PostAction(1); return Task.CompletedTask; },
            (dialog, title, text, indeterminate, position, cancel, token) => Task.CompletedTask,
            (dialog, position, text) => Task.CompletedTask);

        var name = libvlc.RendererList.FirstOrDefault(r => r.Name?.Contains("microdns", StringComparison.OrdinalIgnoreCase) == true).Name ?? "microdns";
        var d = new RendererDiscoverer(libvlc, name);
        d.ItemAdded += (_, e) => { lock (Lock) Items.Add(e.RendererItem); Changed?.Invoke(); };
        d.ItemDeleted += (_, e) => { lock (Lock) Items.RemoveAll(i => i.Name == e.RendererItem.Name); Changed?.Invoke(); };
        d.Start();
        _discoverer = d;
    }
}
