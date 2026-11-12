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

    public SettingsViewModel(AppSession session, UpdateViewModel updates)
    {
        _session = session;
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

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root);
}
