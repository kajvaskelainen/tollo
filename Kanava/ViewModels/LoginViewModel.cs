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

    /// <summary>Tallennettu salasana kelpaa, jos palvelin ja tunnus ovat samat kuin tallennetut.</summary>
    private Credentials? SavedCredentials()
    {
        var saved = _settings.GetSavedCredentials();
        return saved is not null && saved.Username == Username.Trim() &&
               string.Equals(saved.ServerUrl.TrimEnd('/'), ServerUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? saved : null;
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
    }

    private bool CanLogin() => !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LoginAsync()
    {
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
                    account = await new XtreamClient(creds).LoginAsync();
                    break;
                }
                catch (XtreamException ex) when (ex.IsTransient && attempt < waits.Length)
                {
                    BusyText = $"Odotetaan verkkoyhteyttä… (yritys {attempt + 2}/{waits.Length + 1})";
                    await Task.Delay(TimeSpan.FromSeconds(waits[attempt]));
                }
            }
            if (save) _settings.SaveCredentials(creds, RememberMe);
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
