using System.IO;
using System.Windows.Threading;
using Kanava.Models;
using LibVLCSharp.Shared;

namespace Kanava.Services;

/// <summary>
/// Live-lähetysten tallennus .ts-tiedostoon (ilman uudelleenpakkausta). Ajastetut tallennukset
/// alkavat minuuttia ennen ohjelmaa ja jatkuvat 2 min yli. Tallennus käyttää oman yhteyden palvelimeen.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private static readonly TimeSpan PreRoll = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PostRoll = TimeSpan.FromMinutes(2);

    private readonly AppSession _session;
    private readonly Dictionary<Guid, MediaPlayer> _active = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>Tallennusten tila muuttui (UI päivittyy).</summary>
    public event Action? Changed;

    public RecordingService(AppSession session)
    {
        _session = session;
        // Edellisen käynnistyksen kesken jääneet: merkitään epäonnistuneiksi tai jatketaan
        _session.UserData.UpdateRecordings(list =>
        {
            foreach (var r in list.Where(r => r.Status == "Recording"))
            {
                r.Status = r.End > DateTime.Now ? "Scheduled" : "Failed";
                if (r.Status == "Failed") r.Error = "Töllö suljettiin kesken tallennuksen";
            }
        });
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public string Folder
    {
        get
        {
            var f = _session.Settings.Settings.RecordingFolder;
            return string.IsNullOrWhiteSpace(f)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Töllö")
                : f;
        }
    }

    public IReadOnlyList<Recording> All => _session.UserData.Recordings;

    public Recording? ActiveFor(int streamId) => All.FirstOrDefault(r => r.StreamId == streamId && r.Status == "Recording");

    public bool IsScheduled(int streamId, DateTime start) =>
        All.Any(r => r.StreamId == streamId && r.Start == start && r.Status is "Scheduled" or "Recording");

    private const string ManualTitle = "Tallennus";

    /// <summary>Aloittaa kanavan tallennuksen heti (enintään 4 h, lopetetaan StopAsync:lla).</summary>
    public Recording RecordNow(LiveChannel ch)
    {
        var rec = new Recording { StreamId = ch.StreamId, ChannelName = ch.Name, Title = ManualTitle, Start = DateTime.Now, End = DateTime.Now.AddHours(4) };
        _session.UserData.UpdateRecordings(l => l.Add(rec));
        StartRecording(rec);
        return rec;
    }

    /// <summary>Ajastaa ohjelman tallennuksen; jos jo ajastettu, peruu sen. Palauttaa true, jos ajastettu.</summary>
    public bool ToggleSchedule(LiveChannel ch, EpgProgram p)
    {
        var existing = All.FirstOrDefault(r => r.StreamId == ch.StreamId && r.Start == p.Start && r.Status is "Scheduled" or "Recording");
        if (existing is not null) { Cancel(existing.Id); return false; }
        var rec = new Recording { StreamId = ch.StreamId, ChannelName = ch.Name, Title = p.Title, Start = p.Start, End = p.End };
        _session.UserData.UpdateRecordings(l => l.Add(rec));
        Tick();
        return true;
    }

    /// <summary>Lopettaa käynnissä olevan (valmis) tai peruu ajastetun.</summary>
    public void Cancel(Guid id)
    {
        var wasActive = _active.ContainsKey(id);
        StopPlayer(id);
        _session.UserData.UpdateRecordings(l =>
        {
            if (l.FirstOrDefault(r => r.Id == id) is { } r)
            {
                r.Status = wasActive ? "Done" : "Cancelled";
                if (wasActive) r.End = DateTime.Now;
            }
        });
        Changed?.Invoke();
    }

    public void ClearFinished()
    {
        _session.UserData.UpdateRecordings(l => l.RemoveAll(r => r.Status is "Done" or "Failed" or "Cancelled"));
        Changed?.Invoke();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var changed = false;
        foreach (var r in All)
        {
            if (r.Status == "Scheduled" && now >= r.Start - PreRoll && now < r.End + PostRoll) { StartRecording(r); changed = true; }
            else if (r.Status == "Scheduled" && now >= r.End + PostRoll)
            {
                _session.UserData.UpdateRecordings(l => { if (l.FirstOrDefault(x => x.Id == r.Id) is { } x) { x.Status = "Failed"; x.Error = "Töllö ei ollut käynnissä"; } });
                changed = true;
            }
            else if (r.Status == "Recording" && now >= r.End + (r.Title == ManualTitle ? TimeSpan.Zero : PostRoll))
            {
                StopPlayer(r.Id);
                _session.UserData.UpdateRecordings(l => { if (l.FirstOrDefault(x => x.Id == r.Id) is { } x) x.Status = "Done"; });
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void StartRecording(Recording rec)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, SafeName($"{rec.ChannelName} {rec.Start:yyyy-MM-dd HHmm} {rec.Title}") + ".ts");
            var url = _session.Client.LiveUrl(rec.StreamId, "ts");
            var player = new MediaPlayer(_session.Player.LibVLC);
            using (var media = new Media(_session.Player.LibVLC, new Uri(url)))
            {
                media.AddOption(":http-reconnect");
                media.AddOption($":sout=#std{{access=file,mux=ts,dst=\"{file}\"}}");
                media.AddOption(":sout-keep");
                player.EncounteredError += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Failed(rec.Id, "Lähetys katkesi"));
                player.Play(media);
            }
            _active[rec.Id] = player;
            _session.UserData.UpdateRecordings(l =>
            {
                if (l.FirstOrDefault(x => x.Id == rec.Id) is { } x) { x.Status = "Recording"; x.File = file; }
            });
            rec.Status = "Recording";
            rec.File = file;
            Changed?.Invoke();
        }
        catch (Exception ex) { Failed(rec.Id, ex.Message); }
    }

    private void Failed(Guid id, string error)
    {
        StopPlayer(id);
        _session.UserData.UpdateRecordings(l =>
        {
            if (l.FirstOrDefault(x => x.Id == id) is { } x && x.Status != "Done") { x.Status = "Failed"; x.Error = error; }
        });
        Changed?.Invoke();
    }

    private void StopPlayer(Guid id)
    {
        if (!_active.Remove(id, out var p)) return;
        Task.Run(() => { try { p.Stop(); p.Dispose(); } catch { } });
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 120 ? s[..120] : s;
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var id in _active.Keys.ToList())
        {
            StopPlayer(id);
            _session.UserData.UpdateRecordings(l => { if (l.FirstOrDefault(x => x.Id == id) is { } x) x.Status = "Done"; });
        }
        _session.UserData.SaveNow();
    }
}
