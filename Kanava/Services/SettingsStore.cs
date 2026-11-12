using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kanava.Models;

namespace Kanava.Services;

public static class AppPaths
{
    private const string Name = "Tollo";
    /// <summary>Ohjelman aiempi nimi; sen kansiot siirretään uuteen nimeen ensimmäisellä käynnistyksellä.</summary>
    private const string OldName = "Kanava";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string Cache { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "cache");

    static AppPaths()
    {
        Migrate(Environment.SpecialFolder.ApplicationData);
        Migrate(Environment.SpecialFolder.LocalApplicationData);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
    }

    private static void Migrate(Environment.SpecialFolder folder)
    {
        try
        {
            var root = Environment.GetFolderPath(folder);
            string from = Path.Combine(root, OldName), to = Path.Combine(root, Name);
            if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
        }
        catch { /* esim. vanha versio vielä käynnissä – aloitetaan tyhjästä */ }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string UserDataFile(string profileKey) => Path.Combine(Root, $"userdata_{profileKey}.json");
    public static string EpgFile(string profileKey) => Path.Combine(Cache, $"epg_{profileKey}.xml");
}

public sealed class AppSettings
{
    public string? ServerUrl { get; set; }
    public string? Username { get; set; }
    /// <summary>Salasana DPAPI:lla (CurrentUser) salattuna, Base64.</summary>
    public string? PasswordProtected { get; set; }
    public bool RememberLogin { get; set; } = true;
    public string LiveFormat { get; set; } = "ts";
    public int Volume { get; set; } = 80;
    /// <summary>Viimeksi valittu ääniraidan kieli – valitaan automaattisesti, jos löytyy.</summary>
    public string PreferredAudioLanguage { get; set; } = "";
    /// <summary>Viimeksi valittu tekstityskieli ("" = ei tekstitystä).</summary>
    public string PreferredSubtitleLanguage { get; set; } = "";
    /// <summary>Tarkistetaanko päivitykset käynnistyksessä.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Versio, jota käyttäjä pyysi olemaan tarjoamatta.</summary>
    public string SkippedVersion { get; set; } = "";
}

/// <summary>Asetusten ja kirjautumistietojen tallennus %AppData%\Tollo -kansioon.</summary>
public sealed class SettingsStore
{
    private static readonly byte[] Entropy = "Kanava.IPTV.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { Settings = new(); }
    }

    public void Save()
    {
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }

    public Credentials? GetSavedCredentials()
    {
        var s = Settings;
        if (string.IsNullOrEmpty(s.ServerUrl) || string.IsNullOrEmpty(s.Username) || string.IsNullOrEmpty(s.PasswordProtected))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(s.PasswordProtected), Entropy, DataProtectionScope.CurrentUser);
            return new Credentials { ServerUrl = s.ServerUrl, Username = s.Username, Password = Encoding.UTF8.GetString(bytes) };
        }
        catch { return null; }
    }

    public void SaveCredentials(Credentials c, bool remember)
    {
        Settings.ServerUrl = c.ServerUrl;
        Settings.Username = c.Username;
        Settings.RememberLogin = remember;
        Settings.PasswordProtected = remember
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(c.Password), Entropy, DataProtectionScope.CurrentUser))
            : null;
        Save();
    }

    public void ClearPassword()
    {
        Settings.PasswordProtected = null;
        Save();
    }
}
