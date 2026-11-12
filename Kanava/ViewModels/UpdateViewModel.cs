using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Services;

namespace Kanava.ViewModels;

/// <summary>Päivitysten tarkistus ja asennus (ilmoituspalkki + Asetukset-kortti).</summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    [ObservableProperty] private UpdateInfo? _available;
    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _error;

    public UpdateViewModel(SettingsStore settings) => _settings = settings;

    public string CurrentVersion => UpdateService.CurrentVersion.ToString(3);
    public string AvailableVersion => Available?.Version.ToString(3) ?? "";
    public bool CanSelfUpdate { get; } = UpdateService.CanSelfUpdate;
    public string InstallLabel => CanSelfUpdate ? "Päivitä nyt" : "Lataa";

    public bool AutoCheck
    {
        get => _settings.Settings.CheckUpdates;
        set { _settings.Settings.CheckUpdates = value; _settings.Save(); OnPropertyChanged(); }
    }

    partial void OnAvailableChanged(UpdateInfo? value) => OnPropertyChanged(nameof(AvailableVersion));

    /// <summary>Käynnistyksen yhteydessä: hiljainen tarkistus, ei virheilmoituksia.</summary>
    public async Task StartupCheckAsync()
    {
        UpdateService.CleanupOldFiles();
        if (!AutoCheck) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null || info.Version.ToString(3) == _settings.Settings.SkippedVersion) return;
            Available = info;
            IsBannerVisible = true;
        }
        catch { /* ei verkkoa tms. – yritetään seuraavalla kerralla */ }
    }

    [RelayCommand]
    private async Task CheckNow()
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        Status = "Tarkistetaan…";
        try
        {
            Available = await UpdateService.CheckAsync();
            Status = Available is null ? $"Käytössäsi on uusin versio ({CurrentVersion})." : $"Uusi versio {AvailableVersion} on saatavilla.";
            if (Available is not null) IsBannerVisible = true;
        }
        catch (Exception ex) { Status = ""; Error = "Tarkistus epäonnistui: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Install()
    {
        if (Available is not { } info || IsBusy) return;
        if (!CanSelfUpdate) { UpdateService.OpenPage(info.PageUrl); return; }

        IsBusy = true;
        IsBannerVisible = true;
        Error = null;
        Progress = 0;
        Status = "Ladataan päivitystä…";
        try
        {
            var p = new Progress<double>(v =>
            {
                Progress = v;
                Status = v < 0.9 ? $"Ladataan päivitystä… {v / 0.9:P0}" : "Asennetaan…";
            });
            var exe = await UpdateService.InstallAsync(info, p);
            Status = "Käynnistetään uudelleen…";
            UpdateService.Restart(exe);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Error = "Päivitys epäonnistui: " + ex.Message;
            Status = "";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenNotes() => UpdateService.OpenPage(Available?.PageUrl);

    [RelayCommand]
    private void Skip()
    {
        if (Available is not null)
        {
            _settings.Settings.SkippedVersion = AvailableVersion;
            _settings.Save();
        }
        IsBannerVisible = false;
    }

    [RelayCommand]
    private void Dismiss() => IsBannerVisible = false;
}
