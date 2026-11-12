using System.Globalization;
using System.Text.Json;

namespace Kanava.Services;

/// <summary>
/// Xtream-palvelimet palauttavat kenttiä vaihtelevasti merkkijonoina, numeroina tai null-arvoina.
/// Nämä apufunktiot lukevat arvot sallivasti.
/// </summary>
internal static class JsonX
{
    public static string Str(this JsonElement e, string name, string fallback = "")
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return fallback;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? fallback,
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => fallback
        };
    }

    public static string? StrOrNull(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static int Int(this JsonElement e, string name, int fallback = 0)
    {
        var s = e.Str(name);
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)d;
        return fallback;
    }

    public static long Long(this JsonElement e, string name, long fallback = 0)
    {
        var s = e.Str(name);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public static double Dbl(this JsonElement e, string name, double fallback = 0)
    {
        var s = e.Str(name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public static DateTime? UnixTime(this JsonElement e, string name)
    {
        var v = e.Long(name);
        return v > 0 ? DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime : null;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) yield return x;
        else if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) yield return p.Value;
    }

    public static string FirstStr(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return "";
        if (p.ValueKind == JsonValueKind.Array)
            foreach (var x in p.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    return x.GetString()!;
        return e.Str(name);
    }
}
