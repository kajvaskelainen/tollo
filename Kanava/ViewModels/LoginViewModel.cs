using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly Func<Credentials, AccountInfo, Task> _onSuccess;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] private string _serverUrl = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] private string _username = "";
    [ObservableProperty] private bool _rememberMe = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] private bool _isBusy;
    [ObservableProperty] private string _busyText = "Kirjaudutaan…";

    /// <summary>Salasana asetetaan PasswordBoxista (ei sidota, jotta se ei jää näkyviin).</summary>
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(nameof(ShowSavedPasswordHint)); }
    }
    private string _password = "";

    /// <summary>Tallennettu salasana kelpaa, jos samalle palvelimelle ja tunnukselle on tallennettu tili.</summary>
    private Credentials? SavedCredentials() =>
        _settings.FindAccount("xtream", ServerUrl, Username.Trim()) is { } a ? _settings.GetCredentials(a) : null;

    // ---------- Tilin tyyppi ja M3U ----------

    /// <summary>"xtream" tai "m3u".</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] private string _mode = "xtream";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] private string _m3uUrl = "";
    [ObservableProperty] private string _m3uName = "";

    public bool IsXtream { get => Mode == "xtream"; set { if (value) Mode = "xtream"; } }
    public bool IsM3u { get => Mode == "m3u"; set { if (value) Mode = "m3u"; } }

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsXtream));
        OnPropertyChanged(nameof(IsM3u));
        Error = null;
    }

    [RelayCommand]
    private void BrowseM3u()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Valitse soittolista", Filter = "Soittolistat (*.m3u;*.m3u8)|*.m3u;*.m3u8|Kaikki tiedostot (*.*)|*.*" };
        if (dlg.ShowDialog() == true) M3uUrl = dlg.FileName;
    }

    // ---------- Tallennetut tilit ----------

    public System.Collections.ObjectModel.ObservableCollection<SavedAccountRow> Accounts { get; } = [];
    public bool HasAccounts => Accounts.Count > 0;

    private void RefreshAccounts()
    {
        Accounts.Clear();
        foreach (var a in _settings.Accounts) Accounts.Add(new SavedAccountRow(a));
        OnPropertyChanged(nameof(HasAccounts));
    }

    [RelayCommand]
    private Task LoginSaved(SavedAccountRow? row)
    {
        if (row is null || IsBusy) return Task.CompletedTask;
        if (_settings.GetCredentials(row.Account) is { } creds) return TryLoginAsync(creds, save: true);
        // Salasanaa ei ole tallennettu: täytetään lomake
        Mode = "xtream";
        ServerUrl = row.Account.ServerUrl;
        Username = row.Account.Username;
        Error = "Anna salasana ja kirjaudu.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveAccount(SavedAccountRow? row)
    {
        if (row is null) return;
        _settings.RemoveAccount(row.Account.Id);
        RefreshAccounts();
        OnPropertyChanged(nameof(ShowSavedPasswordHint));
    }

    /// <summary>Salasanakenttä on tyhjä, mutta tallennettu salasana on käytettävissä.</summary>
    public bool ShowSavedPasswordHint => Password.Length == 0 && SavedCredentials() is not null;

    partial void OnServerUrlChanged(string value) => OnPropertyChanged(nameof(ShowSavedPasswordHint));
    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(ShowSavedPasswordHint));

    public LoginViewModel(SettingsStore settings, Func<Credentials, AccountInfo, Task> onSuccess)
    {
        _settings = settings;
        _onSuccess = onSuccess;
        ServerUrl = settings.Settings.ServerUrl ?? "";
        Username = settings.Settings.Username ?? "";
        RememberMe = settings.Settings.RememberLogin;
        RefreshAccounts();
    }

    private bool CanLogin() => !IsBusy && (IsM3u ? !string.IsNullOrWhiteSpace(M3uUrl) : !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Username));

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LoginAsync()
    {
        if (IsM3u)
            return TryLoginAsync(new Credentials { Kind = "m3u", ServerUrl = M3uUrl.Trim().Trim('"'), Name = M3uName.Trim() }, save: true);

        // Tyhjä salasanakenttä + tallennettu salasana samalle tilille → käytetään tallennettua
        if (Password.Length == 0 && SavedCredentials() is { } saved)
            return TryLoginAsync(saved, save: true);

        var url = ServerUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Error = "Palvelimen osoite ei kelpaa.";
            return Task.CompletedTask;
        }
        // Käyttäjä voi liittää osoitteen muodossa http://x:8080/player_api.php – siistitään
        var clean = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.Replace("player_api.php", "").TrimEnd('/');
        return TryLoginAsync(new Credentials { ServerUrl = clean, Username = Username.Trim(), Password = Password }, save: true);
    }

    /// <summary>
    /// Kirjautuminen. Automaattisessa kirjautumisessa (<paramref name="automatic"/>) verkkovirheitä
    /// yritetään uudelleen, koska koneen käynnistyttyä yhteys ei välttämättä ole vielä valmis.
    /// </summary>
    public async Task TryLoginAsync(Credentials creds, bool save, bool automatic = false)
    {
        Error = null;
        IsBusy = true;
        BusyText = "Kirjaudutaan…";
        int[] waits = automatic ? [2, 3, 5, 10, 15] : [];
        try
        {
            AccountInfo account;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    account = await XtreamClient.Create(creds).LoginAsync();
                    break;
                }
                catch (XtreamException ex) when (ex.IsTransient && attempt < waits.Length)
                {
                    BusyText = $"Odotetaan verkkoyhteyttä… (yritys {attempt + 2}/{waits.Length + 1})";
                    await Task.Delay(TimeSpan.FromSeconds(waits[attempt]));
                }
            }
            if (save) { _settings.SaveCredentials(creds, RememberMe); RefreshAccounts(); }
            BusyText = "Ladataan kanavia…";
            await _onSuccess(creds, account);
        }
        catch (XtreamException ex) { Error = AutoPrefix(automatic) + ex.Message + AutoSuffix(automatic); }
        catch (Exception ex) { Error = AutoPrefix(automatic) + "Odottamaton virhe: " + ex.Message + AutoSuffix(automatic); }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowSavedPasswordHint));
        }
    }

    private static string AutoPrefix(bool automatic) => automatic ? "Automaattinen kirjautuminen epäonnistui: " : "";
    private static string AutoSuffix(bool automatic) =>
        automatic ? "\nTunnuksesi ovat tallessa – yritä uudelleen painamalla Kirjaudu sisään." : "";
}

/// <summary>Tallennettu tili kirjautumisruudun listassa.</summary>
public sealed class SavedAccountRow(SavedAccount account)
{
    public SavedAccount Account { get; } = account;
    public string Title => Account.Name.Length > 0 ? Account.Name
        : Account.Kind == "m3u" ? System.IO.Path.GetFileName(Account.ServerUrl.TrimEnd('/')) is { Length: > 0 } f ? f : "M3U-soittolista"
        : Account.Username;
    public string Subtitle => Account.Kind == "m3u" ? "M3U · " + Account.ServerUrl : Host(Account.ServerUrl) + (Account.PasswordProtected is null ? " · salasana ei tallessa" : "");
    public string Glyph => Account.Kind == "m3u" ? "\uE8FD" : "\uE77B";
    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
