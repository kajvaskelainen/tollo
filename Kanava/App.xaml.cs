using System.Windows;
using System.Windows.Threading;
using Kanava.Models;
using Kanava.Services;
using Kanava.ViewModels;

namespace Kanava;

public partial class App : Application
{
    private PlayerService? _player;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        LibVLCSharp.Shared.Core.Initialize();

        var settings = new SettingsStore();
        settings.Load();
        _player = new PlayerService();
        PlayerService.NormalizeAudio = settings.Settings.NormalizeAudio;
        _main = new MainViewModel(settings, _player);

        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Show();

        _ = _main.StartAsync(ParseArgs(e.Args));
    }

    /// <summary>
    /// Kehityskäyttöön: Tollo.exe --server URL --user U --pass P.
    /// Vain Debug-versiossa – julkaistussa versiossa salasana komentorivillä olisi tietoturvariski.
    /// </summary>
    private static Credentials? ParseArgs(string[] args)
    {
#if !DEBUG
        return null;
#else
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var server = Get("--server");
        var user = Get("--user");
        var pass = Get("--pass");
        return server is null || user is null || pass is null
            ? null
            : new Credentials { ServerUrl = server.TrimEnd('/'), Username = user, Password = pass };
#endif
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.Cache, "error.log"), $"{DateTime.Now:G} {e.Exception}\n\n"); } catch { }
        MessageBox.Show(e.Exception.Message, "Töllö – virhe", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Shutdown();
        try { _player?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
