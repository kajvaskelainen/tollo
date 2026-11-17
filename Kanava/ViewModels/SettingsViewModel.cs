using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSession _session;

    [ObservableProperty] private string _liveFormat;
    [ObservableProperty] private string _epgStatus = "";
    [ObservableProperty] private string _message = "";

    public UpdateViewModel Updates { get; }
    public EpgSourcesViewModel EpgSources { get; }

    private readonly MainViewModel _main;

    public SettingsViewModel(AppSession session, UpdateViewModel updates, MainViewModel main)
    {
        _session = session;
        _main = main;
        if (main.Recordings is { } recs) recs.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshRecordings);
        RefreshRecordings();
        Updates = updates;
        EpgSources = new EpgSourcesViewModel(session);
        _liveFormat = session.Settings.Settings.LiveFormat;
        EpgStatus = session.Epg.Status;
        session.Epg.Updated += OnEpgUpdated;
    }

    private void OnEpgUpdated() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => EpgStatus = _session.Epg.Status);

    public AccountInfo Account => _session.Account;
    public string Server => _session.Credentials.BaseUrl;
    public string ExpiresText => Account.ExpiresAt is { } d ? d.ToString("d.M.yyyy") : "Ei vanhenemispäivää";
    public string ConnectionsText => $"{Account.ActiveConnections} / {Account.MaxConnections}";
    public string Counts => $"{_session.LiveChannels.Count} kanavaa · {_session.LiveCategories.Count} kategoriaa";
    public string DataFolder => AppPaths.Root;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public bool IsTs { get => LiveFormat == "ts"; set { if (value) LiveFormat = "ts"; } }
    public bool IsHls { get => LiveFormat == "m3u8"; set { if (value) LiveFormat = "m3u8"; } }

    partial void OnLiveFormatChanged(string value)
    {
        _session.Settings.Settings.LiveFormat = value;
        _session.Settings.Save();
        OnPropertyChanged(nameof(IsTs));
        OnPropertyChanged(nameof(IsHls));
        Message = "Striimimuoto vaihdettu – koskee seuraavaa kanavanvaihtoa.";
    }

    // ---------- Pienoissoitin ----------

    private AppSettings S => _session.Settings.Settings;

    private void SetPip(Action<AppSettings> change, string message)
    {
        change(S);
        _session.Settings.Save();
        OnPropertyChanged(string.Empty); // valintanapit päivittyvät
        Message = message;
    }

    private bool Mode(string m) => S.PipMode == m;
    private void SetMode(string m) => SetPip(s => s.PipMode = m, "Pienoissoittimen asetus tallennettu.");
    public bool PipOnMinimize { get => Mode("minimize"); set { if (value) SetMode("minimize"); } }
    public bool PipOnClose { get => Mode("close"); set { if (value) SetMode("close"); } }
    public bool PipManual { get => Mode("manual"); set { if (value) SetMode("manual"); } }

    private void SetCorner(string c) => SetPip(s => s.PipCorner = c, "Pienoissoittimen paikka tallennettu.");
    public bool PipTopLeft { get => S.PipCorner == "TopLeft"; set { if (value) SetCorner("TopLeft"); } }
    public bool PipTopRight { get => S.PipCorner == "TopRight"; set { if (value) SetCorner("TopRight"); } }
    public bool PipBottomLeft { get => S.PipCorner == "BottomLeft"; set { if (value) SetCorner("BottomLeft"); } }
    public bool PipBottomRight { get => S.PipCorner == "BottomRight"; set { if (value) SetCorner("BottomRight"); } }

    private void SetSize(string z) => SetPip(s => s.PipSize = z, "Pienoissoittimen koko tallennettu.");
    public bool PipSmall { get => S.PipSize == "s"; set { if (value) SetSize("s"); } }
    public bool PipMedium { get => S.PipSize is not ("s" or "l"); set { if (value) SetSize("m"); } }
    public bool PipLarge { get => S.PipSize == "l"; set { if (value) SetSize("l"); } }

    public bool PipTopmost
    {
        get => S.PipTopmost;
        set => SetPip(s => s.PipTopmost = value, value ? "Pienoissoitin pysyy muiden ikkunoiden päällä." : "Pienoissoitin voi jäädä muiden ikkunoiden alle.");
    }

    [ObservableProperty] private bool _isReloadingChannels;
    [ObservableProperty] private string _channelStatus = "";

    /// <summary>Kanavalista ladattu uudelleen – Live TV päivitetään, kun osiota vaihdetaan.</summary>
    public bool ChannelsReloaded { get; set; }

    [RelayCommand]
    private async Task ReloadChannels()
    {
        if (IsReloadingChannels) return;
        IsReloadingChannels = true;
        Message = "";
        ChannelStatus = "Ladataan kanavalistaa palvelimelta…";
        try
        {
            ChannelStatus = await _session.ReloadLiveAsync();
            ChannelsReloaded = true;
            OnPropertyChanged(nameof(Counts));
        }
        catch (Exception ex) { ChannelStatus = "Lataus epäonnistui: " + ex.Message; }
        finally { IsReloadingChannels = false; }
    }

    [RelayCommand]
    private async Task RefreshEpg()
    {
        Message = "";
        await _session.Epg.LoadAsync(force: true);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _session.UserData.ClearHistory();
        Message = "Katseluhistoria tyhjennetty.";
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.Cache, "*.download")) File.Delete(f);
            Message = "Väliaikaistiedostot poistettu.";
        }
        catch (Exception ex) { Message = "Poisto epäonnistui: " + ex.Message; }
    }

    // ---------- Tallennukset ----------

    public System.Collections.ObjectModel.ObservableCollection<RecordingRow> RecordingRows { get; } = [];
    public bool HasRecordings => RecordingRows.Count > 0;
    public string RecordingFolder => _main.Recordings?.Folder ?? "";

    private void RefreshRecordings()
    {
        RecordingRows.Clear();
        if (_main.Recordings is not { } recs) return;
        foreach (var r in recs.All.OrderBy(r => r.Status switch { "Recording" => 0, "Scheduled" => 1, _ => 2 }).ThenByDescending(r => r.Start))
            RecordingRows.Add(new RecordingRow(r, () => recs.Cancel(r.Id)));
        OnPropertyChanged(nameof(HasRecordings));
    }

    [RelayCommand]
    private void ChooseRecordingFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Valitse tallennusten kansio", InitialDirectory = RecordingFolder };
        if (dlg.ShowDialog() != true) return;
        _session.Settings.Settings.RecordingFolder = dlg.FolderName;
        _session.Settings.Save();
        OnPropertyChanged(nameof(RecordingFolder));
        Message = "Tallennusten kansio vaihdettu.";
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            System.Diagnostics.Process.Start("explorer.exe", RecordingFolder);
        }
        catch (Exception ex) { Message = "Kansiota ei voitu avata: " + ex.Message; }
    }

    [RelayCommand]
    private void ClearFinishedRecordings() => _main.Recordings?.ClearFinished();

    // ---------- Varmuuskopio ----------

    [RelayCommand]
    private void ExportBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Vie Töllön varmuuskopio",
            FileName = $"Tollo-varmuuskopio-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "Töllön varmuuskopio (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _session.UserData.SaveNow();
            BackupService.Export(dlg.FileName, _session.Settings, _session.UserData);
            Message = "Varmuuskopio tallennettu: " + Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex) { Message = "Vienti epäonnistui: " + ex.Message; }
    }

    [RelayCommand]
    private void ImportBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Tuo Töllön varmuuskopio", Filter = "Töllön varmuuskopio (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        var ok = System.Windows.MessageBox.Show(
            "Varmuuskopio korvaa nykyiset suosikit, piilotukset, järjestykset, katselukohdat ja asetukset. Kirjautumistiedot säilyvät.\n\nTöllö käynnistyy uudelleen. Jatketaanko?",
            "Tuo varmuuskopio", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) return;
        try
        {
            BackupService.Import(dlg.FileName, _session.Settings, _session.UserData);
            // Uudelleenkäynnistys lataa kaiken varmuuskopiosta
            if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) { Message = "Tuonti epäonnistui: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root);
}

/// <summary>Tallennus asetusten listassa.</summary>
public sealed class RecordingRow(Recording r, Action cancel)
{
    public string Title => r.Title;
    public string Subtitle => $"{r.ChannelName} · {r.Start:ddd d.M. HH.mm}–{r.End:HH.mm}";
    public string StatusText => r.Status switch
    {
        "Scheduled" => "Ajastettu",
        "Recording" => "● Tallentaa",
        "Done" => "Valmis",
        "Failed" => "Epäonnistui" + (r.Error is { } e ? ": " + e : ""),
        _ => "Peruttu",
    };
    public bool CanCancel => r.Status is "Scheduled" or "Recording";
    public string CancelText => r.Status == "Recording" ? "Lopeta" : "Peru";
    public bool HasFile => r.Status == "Done" && r.File is { } f && File.Exists(f);
    public IRelayCommand CancelCommand { get; } = new RelayCommand(cancel);
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() =>
    {
        if (r.File is { } f && File.Exists(f)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{f}\"");
    });
}
