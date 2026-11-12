using LibVLCSharp.Shared;

namespace Kanava.Services;

/// <summary>Yhteinen LibVLC-instanssi ja esikatselusoitin.</summary>
public sealed class PlayerService : IDisposable
{
    public LibVLC LibVLC { get; }
    public MediaPlayer Preview { get; }

    public PlayerService()
    {
        LibVLC = new LibVLC(
            "--network-caching=1500",
            "--live-caching=1500",
            "--no-video-title-show",
            "--no-osd",
            "--no-snapshot-preview");
        Preview = CreatePlayer();
    }

    public MediaPlayer CreatePlayer() => new(LibVLC)
    {
        EnableHardwareDecoding = true,
        // Hiiri- ja näppäintapahtumat WPF:lle, ei VLC:n omalle ikkunalle
        EnableMouseInput = false,
        EnableKeyInput = false,
    };

    private readonly object _queueLock = new();
    private Task _queue = Task.CompletedTask;

    /// <summary>VLC:n kutsut voivat blokata hetken – ajetaan järjestyksessä taustasäikeessä, ettei UI jäädy.</summary>
    private void Enqueue(Action action)
    {
        lock (_queueLock)
            _queue = _queue.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }, TaskScheduler.Default);
    }

    public void Play(MediaPlayer player, string url) => Enqueue(() =>
    {
        using var media = new Media(LibVLC, new Uri(url));
        media.AddOption(":http-reconnect");
        player.Play(media);
    });

    public void Stop(MediaPlayer player) => Enqueue(player.Stop);

    public void Dispose()
    {
        Preview.Stop();
        Preview.Dispose();
        LibVLC.Dispose();
    }
}
