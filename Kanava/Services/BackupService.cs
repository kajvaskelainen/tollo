using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kanava.Services;

/// <summary>
/// Varmuuskopio: suosikit, piilotukset, järjestykset, katselukohdat, muistutukset, tallennusajastukset
/// ja asetukset yhteen JSON-tiedostoon. Kirjautumistietoja (salasanaa) ei viedä.
/// </summary>
public static class BackupService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private static readonly string[] SecretKeys = ["ServerUrl", "Username", "PasswordProtected", "Accounts", "AutoLoginAccountId", "RememberLogin"];

    public static void Export(string file, SettingsStore settings, UserDataStore userData)
    {
        var s = JsonNode.Parse(JsonSerializer.Serialize(settings.Settings))!.AsObject();
        foreach (var k in SecretKeys) s.Remove(k);
        var root = new JsonObject
        {
            ["App"] = "Töllö",
            ["Version"] = 1,
            ["Created"] = DateTime.Now.ToString("s"),
            ["Settings"] = s,
            ["UserData"] = JsonNode.Parse(userData.ExportJson()),
        };
        File.WriteAllText(file, root.ToJsonString(Opts));
    }

    /// <summary>Palauttaa varmuuskopion. Kirjautumistiedot säilyvät ennallaan.</summary>
    public static void Import(string file, SettingsStore settings, UserDataStore userData)
    {
        var root = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new InvalidDataException("Tiedosto ei ole Töllön varmuuskopio.");
        if (root["App"]?.GetValue<string>() != "Töllö") throw new InvalidDataException("Tiedosto ei ole Töllön varmuuskopio.");

        if (root["UserData"] is JsonObject ud)
            userData.Import(ud.Deserialize<UserData>() ?? new UserData());

        if (root["Settings"] is JsonObject s)
        {
            // Yhdistetään nykyisten (kirjautumis)tietojen päälle
            var current = JsonNode.Parse(JsonSerializer.Serialize(settings.Settings))!.AsObject();
            foreach (var (k, v) in s)
                if (!SecretKeys.Contains(k)) current[k] = v?.DeepClone();
            var merged = current.Deserialize<AppSettings>();
            if (merged is not null)
            {
                settings.Replace(merged);
                settings.Save();
            }
        }
    }
}
