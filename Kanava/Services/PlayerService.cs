using System.Collections.Concurrent;
using LibVLCSharp.Shared;

namespace Kanava.Services;

/// <summary>
/// Yhteinen LibVLC-instanssi ja esikatselusoitin. VLC-kutsut ajetaan järjestyksessä taustasäikeessä.
/// Live-lähetyksiä valvotaan: jos lähetys katkeaa tai jumittuu, yhdistetään automaattisesti uudelleen.
/// </summary>
public sealed class PlayerService : IDisposable
{
    public LibVLC LibVLC { get; }
    public MediaPlayer Preview { get; }

    /// <summary>Äänenvoimakkuuden tasoitus (normvol) – vaikuttaa seuraavaan Play-kutsuun.</summary>
    public static bool NormalizeAudio { get; set; }

    /// <summary>Uudelleenyhdistys alkaa: (soitin, yritys, yrityksiä enintään). Kutsutaan taustasäikeessä.</summary>
    public event Action<MediaPlayer, int, int>? Reconnecting;
    /// <summary>Uudelleenyhdistys luovutettiin (yritykset loppuivat).</summary>
    public event Action<MediaPlayer>? ReconnectFailed;

    private sealed class Watch
    {
        public required string Url;
        public long LastTime = -1;
        public DateTime LastProgress = DateTime.Now;
        public int Attempt;
        public DateTime NextTry = DateTime.MinValue;
        public DateTime LastAttemptAt = DateTime.MinValue;
        public bool Reconnecting;
    }

    private static readonly int[] Backoff = [2, 3, 5, 8, 12, 20, 30, 30];
    private readonly ConcurrentDictionary<MediaPlayer, Watch> _watched = new();
    private readonly Timer _watchdog;

    public PlayerService()
    {
        LibVLC = new LibVLC(
            "--network-caching=1500",
            "--live-caching=1500",
            "--no-video-title-show",
            "--no-osd",
            "--no-snapshot-preview");
        Preview = CreatePlayer();
        _watchdog = new Timer(_ => CheckStreams(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public MediaPlayer CreatePlayer()
    {
        var p = new MediaPlayer(LibVLC)
        {
            EnableHardwareDecoding = true,
            // Hiiri- ja näppäintapahtumat WPF:lle, ei VLC:n omalle ikkunalle
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        p.Playing += (_, _) => { if (_watched.TryGetValue(p, out var w)) { w.Reconnecting = false; w.LastProgress = DateTime.Now; } };
        return p;
    }

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

    /// <summary>Käynnistää toiston. live = valvotaan ja yhdistetään katkoksen jälkeen uudelleen.</summary>
    public void Play(MediaPlayer player, string url, bool live = true, IEnumerable<string>? options = null)
    {
        if (live) _watched[player] = new Watch { Url = url };
        else _watched.TryRemove(player, out _);
        var opts = options?.ToList() ?? [];
        Enqueue(() => Start(player, url, opts));
    }

    private void Start(MediaPlayer player, string url, List<string> options)
    {
        using var media = new Media(LibVLC, new Uri(url));
        media.AddOption(":http-reconnect");
        if (NormalizeAudio) media.AddOption(":audio-filter=normvol");
        foreach (var o in options) media.AddOption(o);
        player.Play(media);
    }

    public void Stop(MediaPlayer player)
    {
        _watched.TryRemove(player, out _);
        Enqueue(player.Stop);
    }

    /// <summary>Pysäyttää ja vapauttaa soittimen jonossa (esim. pienoissoittimen suljettaessa).</summary>
    public void Release(MediaPlayer player)
    {
        _watched.TryRemove(player, out _);
        Enqueue(() => { player.Stop(); player.Dispose(); });
    }

    /// <summary>Käynnistää valvotun lähetyksen heti uudelleen (esim. asetuksen muutoksen jälkeen).</summary>
    public void Restart(MediaPlayer player)
    {
        if (_watched.TryGetValue(player, out var w)) Enqueue(() => Start(player, w.Url, []));
    }

    // ---------- Valvonta ----------

    private void CheckStreams()
    {
        var now = DateTime.Now;
        foreach (var (player, w) in _watched)
        {
            VLCState state;
            long time;
            try { state = player.State; time = player.Time; }
            catch { continue; }

            if (state is VLCState.Playing && time != w.LastTime)
            {
                // Toisto etenee – nollataan yrityslaskuri hetken vakaan toiston jälkeen
                if (w.LastTime >= 0 && time > w.LastTime) { w.LastProgress = now; if (now - w.NextTry > TimeSpan.FromSeconds(20)) w.Attempt = 0; }
                w.LastTime = time;
                continue;
            }

            bool broken;
            if (w.Reconnecting)
                // Edellinen yritys epäonnistui tai jäi jumiin avaamiseen
                broken = state is VLCState.Error or VLCState.Ended or VLCState.Stopped || now - w.LastAttemptAt > TimeSpan.FromSeconds(20);
            else
                broken = state is VLCState.Error or VLCState.Ended
                         || state is VLCState.Stopped && w.LastTime > 0
                         || state is VLCState.Playing or VLCState.Buffering && w.LastTime > 0 && now - w.LastProgress > TimeSpan.FromSeconds(15);
            if (!broken || now < w.NextTry) continue;

            if (w.Attempt >= Backoff.Length)
            {
                _watched.TryRemove(player, out _);
                ReconnectFailed?.Invoke(player);
                continue;
            }
            w.Attempt++;
            w.Reconnecting = true;
            w.LastTime = -1;
            w.LastProgress = now;
            w.LastAttemptAt = now;
            w.NextTry = now.AddSeconds(Backoff[w.Attempt - 1]);
            Reconnecting?.Invoke(player, w.Attempt, Backoff.Length);
            var url = w.Url;
            Enqueue(() => { player.Stop(); Start(player, url, []); });
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _watched.Clear();
        Preview.Stop();
        Preview.Dispose();
        LibVLC.Dispose();
    }
}
