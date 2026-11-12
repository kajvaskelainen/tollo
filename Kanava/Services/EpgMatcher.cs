using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kanava.Services;

/// <summary>
/// Kanavanimien vertailu ulkoisia ohjelmaoppaita varten. Eri lähteissä sama kanava on esim.
/// "FI: YLE TV1 HD", "YLE TV1", "Yle TV1.fi" tai tunnisteena "YLE.TV1.fi" – kaikista tulee "yletv1".
/// </summary>
public static partial class EpgMatcher
{
    // Maa-/ryhmäetuliite: "FI:", "FI |", "|FI|", "[FIN]", "FI -"
    [GeneratedRegex(@"^\s*[\[\(|]?\s*[a-z]{2,3}\s*[\]\)|:]\s*|^\s*[a-z]{2,3}\s+-\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    // Maatunnus lopussa: "Yle TV1.fi", "MTV3.se"
    [GeneratedRegex(@"\.[a-z]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex CountrySuffix();

    // Laatu- ja tekniikkamerkinnät erillisinä sanoina
    [GeneratedRegex(@"\b(uhd|fhd|hd|sd|4k|hevc|h\.?265|h\.?264|50 ?fps|60 ?fps|backup|raw|vip|multi ?audio)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Quality();

    // Sulkeissa olevat lisätiedot: "(Finland)", "[Backup]"
    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    // Maan nimi viimeisenä sanana: "TLC Suomi" / "TLC Finland" / "Discovery Nordic"
    [GeneratedRegex(@"(suomi|finland|fin|nordic|nordics|scandinavia)$")]
    private static partial Regex CountryWord();

    /// <summary>Väljempi avain toissijaiseen vertailuun (maan nimi lopusta pois, jos jotain jää).</summary>
    public static string Loose(string normalized)
    {
        var s = CountryWord().Replace(normalized, "");
        return s.Length >= 3 ? s : normalized;
    }

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        for (var i = 0; i < 2; i++) s = Prefix().Replace(s, "");
        s = CountrySuffix().Replace(s.Trim(), "");
        s = Bracketed().Replace(s, " ");
        s = Quality().Replace(s, " ");
        s = s.Replace("+", "plus").Replace("&", "and");

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // ä → a
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
