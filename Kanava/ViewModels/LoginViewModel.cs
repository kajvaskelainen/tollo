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
    public string Password { get; set; } = "";

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

    public async Task TryLoginAsync(Credentials creds, bool save)
    {
        Error = null;
        IsBusy = true;
        BusyText = "Kirjaudutaan…";
        try
        {
            var client = new XtreamClient(creds);
            var account = await client.LoginAsync();
            if (save) _settings.SaveCredentials(creds, RememberMe);
            BusyText = "Ladataan kanavia…";
            await _onSuccess(creds, account);
        }
        catch (XtreamException ex) { Error = ex.Message; }
        catch (Exception ex) { Error = "Odottamaton virhe: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
