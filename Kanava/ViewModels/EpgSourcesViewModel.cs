using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>Valmiiksi tunnettu ilmainen XMLTV-lähde.</summary>
public sealed record EpgPreset(string Name, string Url, string Note);

/// <summary>Käytössä oleva ulkoinen lähde asetuksissa.</summary>
public partial class EpgSourceRow : ObservableObject
{
    private readonly EpgSourcesViewModel _owner;
    public EpgSource Source { get; }

    [ObservableProperty] private string _lastResult = "";

    public EpgSourceRow(EpgSourcesViewModel owner, EpgSource source)
    {
        _owner = owner;
        Source = source;
    }

    public string Name => Source.DisplayName;
    public string Url => Source.Url;

    public bool Enabled
    {
        get => Source.Enabled;
        set { if (Source.Enabled == value) return; Source.Enabled = value; OnPropertyChanged(); _owner.SaveAndReload(); }
    }

    [RelayCommand] private Task Test() => _owner.RunTestAsync(Source.Url, Source.DisplayName, this);
    [RelayCommand] private void Remove() => _owner.RemoveRow(this);
}

/// <summary>
/// Ulkoiset ohjelmaoppaat: valmiit ehdotukset, oma osoite, lähteiden testaus (kuinka moni oma
/// kanava saa oppaan) sekä käyttöön otetut lähteet.
/// </summary>
public partial class EpgSourcesViewModel : ObservableObject
{
    private readonly AppSession _session;
    private CancellationTokenSource? _testCts;

    public ObservableCollection<EpgSourceRow> Rows { get; } = [];

    /// <summary>Tarkistettu toimiviksi 11/2026 – sisältö vaihtelee lähteittäin, siksi testaus.</summary>
    public IReadOnlyList<EpgPreset> Presets { get; } =
    [
        new("epgshare01 – Suomi", "https://epgshare01.online/epgshare01/epg_ripper_FI1.xml.gz", "Yle, MTV, Nelonen ja muut kotimaiset"),
        new("open-epg – Suomi 1", "https://www.open-epg.com/files/finland1.xml.gz", "Elokuva-, urheilu- ja dokumenttikanavia"),
        new("open-epg – Suomi 2", "https://www.open-epg.com/files/finland2.xml.gz", ""),
        new("open-epg – Suomi 3", "https://www.open-epg.com/files/finland3.xml.gz", ""),
        new("open-epg – Suomi 4", "https://www.open-epg.com/files/finland4.xml.gz", ""),
    ];

    [ObservableProperty] private string _newUrl = "";
    [ObservableProperty] private bool _isTesting;

    // Testiraportti
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private string _reportSummary = "";
    [ObservableProperty] private string _reportMatched = "";
    [ObservableProperty] private string _reportUnmatched = "";
    [ObservableProperty] private string _reportUrl = "";
    [ObservableProperty] private bool _canAddReported;

    public EpgSourcesViewModel(AppSession session)
    {
        _session = session;
        foreach (var s in session.Settings.Settings.EpgSources) Rows.Add(new EpgSourceRow(this, s));
    }

    public bool HasRows => Rows.Count > 0;

    private bool IsAdded(string url) => Rows.Any(r => string.Equals(r.Url.Trim(), url.Trim(), StringComparison.OrdinalIgnoreCase));

    [RelayCommand] private Task TestPreset(EpgPreset? p) => p is null ? Task.CompletedTask : RunTestAsync(p.Url, p.Name, null);
    [RelayCommand] private void AddPreset(EpgPreset? p) { if (p is not null) Add(p.Url, p.Name); }

    [RelayCommand]
    private Task TestNew()
    {
        var url = NewUrl.Trim();
        return url.Length == 0 ? Task.CompletedTask : RunTestAsync(url, url, null);
    }

    [RelayCommand]
    private void AddNew()
    {
        var url = NewUrl.Trim();
        if (url.Length == 0) return;
        Add(url, "");
        NewUrl = "";
    }

    [RelayCommand]
    private void AddReported()
    {
        var preset = Presets.FirstOrDefault(p => p.Url == ReportUrl);
        Add(ReportUrl, preset?.Name ?? "");
        CanAddReported = false;
    }

    [RelayCommand] private void CloseReport() => HasReport = false;

    private void Add(string url, string name)
    {
        if (IsAdded(url)) { ReportStatus($"Lähde on jo käytössä."); return; }
        var src = new EpgSource { Url = url.Trim(), Name = name };
        _session.Settings.Settings.EpgSources.Add(src);
        Rows.Add(new EpgSourceRow(this, src));
        OnPropertyChanged(nameof(HasRows));
        if (ReportUrl == src.Url) CanAddReported = false;
        SaveAndReload();
    }

    internal void RemoveRow(EpgSourceRow row)
    {
        _session.Settings.Settings.EpgSources.Remove(row.Source);
        Rows.Remove(row);
        OnPropertyChanged(nameof(HasRows));
        SaveAndReload();
    }

    internal void SaveAndReload()
    {
        try { _session.Settings.Save(); } catch { }
        _ = _session.Epg.LoadAsync(force: false);
    }

    private void ReportStatus(string text)
    {
        ReportTitle = text;
        ReportSummary = ReportMatched = ReportUnmatched = "";
        CanAddReported = false;
        HasReport = true;
    }

    internal async Task RunTestAsync(string url, string name, EpgSourceRow? row)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        IsTesting = true;
        ReportUrl = url;
        ReportTitle = $"Testataan: {name}…";
        ReportSummary = "Ladataan ja verrataan kanaviisi – suuret oppaat voivat kestää hetken.";
        ReportMatched = ReportUnmatched = "";
        CanAddReported = false;
        HasReport = true;
        try
        {
            var channels = _session.VisibleLiveChannels.ToList();
            var r = await Task.Run(() => _session.Epg.TestAsync(url, channels, cts.Token));
            if (cts.IsCancellationRequested) return;
            ReportTitle = $"Testi: {name}";
            if (r.Error is not null)
            {
                ReportSummary = "Lataus epäonnistui: " + r.Error;
                if (row is not null) row.LastResult = "Ei toimi: " + r.Error;
                return;
            }

            var lines = new List<string>
            {
                $"Osuu {r.Matched.Count} / {r.TestedChannels} kanavaasi" +
                (r.NewlyCovered > 0 ? $" – näistä {r.NewlyCovered} saisi oppaan, joilla sitä ei nyt ole." : "."),
                $"Lähteessä {r.SourceChannels} kanavaa ja {r.TotalProgrammes} ohjelmaa" +
                (r.From is { } f && r.To is { } t ? $" ({f:d.M. HH:mm} – {t:d.M. HH:mm})." : "."),
                r.ChannelsWithNow > 0
                    ? $"Ohjelma käynnissä juuri nyt {r.ChannelsWithNow} kanavalla – opas on ajan tasalla."
                    : "Yhdelläkään kanavalla ei ole ohjelmaa juuri nyt – opas voi olla vanhentunut tai väärin ajoitettu.",
            };
            var nowMissing = r.Matched.Count(m => !m.HasNow);
            if (r.Matched.Count > 0 && nowMissing > 0)
                lines.Add($"{nowMissing} osuneella kanavalla ei ole ohjelmaa juuri nyt (tauko tai puutteellinen data).");
            ReportSummary = string.Join("\n", lines);

            const int show = 40;
            ReportMatched = r.Matched.Count == 0 ? "" :
                "Osuneet:  " + string.Join(" · ", r.Matched.Take(show).Select(m =>
                    Same(m.Channel.Name, m.SourceName) ? m.Channel.Name : $"{m.Channel.Name} → {m.SourceName}"))
                + (r.Matched.Count > show ? $" … (+{r.Matched.Count - show})" : "");
            ReportUnmatched = r.Unmatched.Count == 0 ? "" :
                "Ilman osumaa:  " + string.Join(" · ", r.Unmatched.Take(show).Select(c => c.Name))
                + (r.Unmatched.Count > show ? $" … (+{r.Unmatched.Count - show})" : "");

            CanAddReported = r.Matched.Count > 0 && !IsAdded(url);
            if (row is not null) row.LastResult = $"Osuu {r.Matched.Count}/{r.TestedChannels} · testattu {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException) { }
        finally { if (_testCts == cts) IsTesting = false; }
    }

    private static bool Same(string a, string b) => EpgMatcher.Normalize(a) == EpgMatcher.Normalize(b) && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
}
