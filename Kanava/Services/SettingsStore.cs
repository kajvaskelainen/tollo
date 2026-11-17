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

    public static string Root { get; } = DevDataDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string Cache { get; } = DevDataDir is { } d ? Path.Combine(d, "cache") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "cache");

    /// <summary>Vain Debug: TOLLO_DATA_DIR ohjaa tiedot erilliseen kansioon testausta varten.</summary>
    private static string? DevDataDir =>
#if DEBUG
        Environment.GetEnvironmentVariable("TOLLO_DATA_DIR") is { Length: > 0 } dir ? dir : null;
#else
        null;
#endif

    static AppPaths()
    {
        if (DevDataDir is not null) { Directory.CreateDirectory(Cache); return; }
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

    // ---------- Pienoissoitin (PiP) ----------
    /// <summary>Milloin pienoissoitin avautuu: "minimize", "close" (myös suljettaessa) tai "manual" (vain painikkeesta).</summary>
    public string PipMode { get; set; } = "minimize";
    /// <summary>TopLeft, TopRight, BottomLeft tai BottomRight.</summary>
    public string PipCorner { get; set; } = "BottomRight";
    /// <summary>"s", "m" tai "l".</summary>
    public string PipSize { get; set; } = "m";
    public bool PipTopmost { get; set; } = true;

    /// <summary>Äänen viive millisekunteina avaimittain ("live:123", "vod:hash"); + = ääni myöhemmin.</summary>
    public Dictionary<string, int> AudioDelays { get; set; } = new();
    /// <summary>Äänenvoimakkuuden tasoitus (mainokset eivät huuda).</summary>
    public bool NormalizeAudio { get; set; }

    /// <summary>Tallennusten kansio ("" = Videot\Töllö).</summary>
    public string RecordingFolder { get; set; } = "";

    /// <summary>Tallennetut tilit (Xtream ja M3U).</summary>
    public List<SavedAccount> Accounts { get; set; } = [];
    /// <summary>Tili, jolla kirjaudutaan automaattisesti käynnistyksessä.</summary>
    public string? AutoLoginAccountId { get; set; }

    /// <summary>Ulkoiset XMLTV-oppaat (täydentävät palveluntarjoajan opasta).</summary>
    public List<EpgSource> EpgSources { get; set; } = [];
}

public sealed class EpgSource
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : Url;
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
        MigrateAccounts();
    }

    /// <summary>Korvaa asetukset (varmuuskopion palautus).</summary>
    public void Replace(AppSettings settings) => Settings = settings;

    public void Save()
    {
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }

    // ---------- Tilit ----------

    private static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    /// <summary>Vanha yhden tilin asetus → tililista (ensimmäinen käynnistys uudella versiolla).</summary>
    private void MigrateAccounts()
    {
        var s = Settings;
        if (s.Accounts.Count > 0 || string.IsNullOrEmpty(s.ServerUrl) || string.IsNullOrEmpty(s.Username)) return;
        var a = new SavedAccount { ServerUrl = s.ServerUrl, Username = s.Username, PasswordProtected = s.PasswordProtected, LastUsed = DateTime.Now };
        s.Accounts.Add(a);
        if (!string.IsNullOrEmpty(s.PasswordProtected) && s.RememberLogin) s.AutoLoginAccountId = a.Id;
    }

    public IReadOnlyList<SavedAccount> Accounts => Settings.Accounts.OrderByDescending(a => a.LastUsed).ToList();

    /// <summary>Tilin tunnukset; null, jos Xtream-tilin salasanaa ei ole tallennettu.</summary>
    public Credentials? GetCredentials(SavedAccount a)
    {
        var password = a.Kind == "m3u" ? "" : Unprotect(a.PasswordProtected);
        if (password is null) return null;
        return new Credentials { Kind = a.Kind, Name = a.Name, ServerUrl = a.ServerUrl, Username = a.Username, Password = password };
    }

    public SavedAccount? FindAccount(string kind, string serverUrl, string username) =>
        Settings.Accounts.FirstOrDefault(a => a.Kind == kind && a.Username == username &&
                                              string.Equals(a.ServerUrl.TrimEnd('/'), serverUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Automaattisen kirjautumisen tili (viimeksi käytetty, jos "Muista kirjautuminen").</summary>
    public Credentials? GetSavedCredentials()
    {
        var a = Settings.Accounts.FirstOrDefault(x => x.Id == Settings.AutoLoginAccountId);
        return a is null ? null : GetCredentials(a);
    }

    public void SaveCredentials(Credentials c, bool remember)
    {
        var a = FindAccount(c.Kind, c.ServerUrl, c.Username);
        if (a is null) { a = new SavedAccount { Kind = c.Kind, ServerUrl = c.ServerUrl, Username = c.Username }; Settings.Accounts.Add(a); }
        if (c.Name.Length > 0) a.Name = c.Name;
        a.LastUsed = DateTime.Now;
        a.PasswordProtected = c.IsM3u ? null : remember ? Protect(c.Password) : null;
        Settings.RememberLogin = remember;
        Settings.AutoLoginAccountId = remember ? a.Id : null;
        // Vanhat kentät (yhteensopivuus aiempien versioiden kanssa)
        if (!c.IsM3u)
        {
            Settings.ServerUrl = c.ServerUrl;
            Settings.Username = c.Username;
            Settings.PasswordProtected = a.PasswordProtected;
        }
        Save();
    }

    public void RemoveAccount(string id)
    {
        Settings.Accounts.RemoveAll(a => a.Id == id);
        if (Settings.AutoLoginAccountId == id) Settings.AutoLoginAccountId = null;
        Save();
    }

    /// <summary>Uloskirjautuminen: ei automaattista kirjautumista seuraavalla käynnistyksellä (tili ja salasana säilyvät listassa).</summary>
    public void ClearPassword()
    {
        Settings.AutoLoginAccountId = null;
        Settings.PasswordProtected = null;
        Save();
    }
}

/// <summary>Tallennettu tili (Xtream Codes tai M3U-soittolista).</summary>
public sealed class SavedAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Kind { get; set; } = "xtream";
    public string Name { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Salasana DPAPI:lla salattuna (null = ei muistettu).</summary>
    public string? PasswordProtected { get; set; }
    public DateTime LastUsed { get; set; }
}
