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

    public SettingsViewModel(AppSession session, UpdateViewModel updates)
    {
        _session = session;
        Updates = updates;
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
