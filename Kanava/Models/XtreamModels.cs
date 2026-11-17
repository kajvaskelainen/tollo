using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanava.Models;

public sealed class Credentials
{
    /// <summary>"xtream" tai "m3u" (ServerUrl = soittolistan osoite tai tiedosto).</summary>
    public string Kind { get; set; } = "xtream";
    /// <summary>Käyttäjän antama nimi tilille (valinnainen).</summary>
    public string Name { get; set; } = "";
    public bool IsM3u => Kind == "m3u";
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Palvelimen osoite ilman loppukauttaviivaa, esim. http://palvelin:8080</summary>
    public string BaseUrl => ServerUrl.TrimEnd('/');

    /// <summary>Tunniste käyttäjäkohtaisille tiedostoille (suosikit, välimuisti).</summary>
    public string ProfileKey
    {
        get
        {
            var bytes = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(BaseUrl.ToLowerInvariant() + "|" + Username));
            return Convert.ToHexString(bytes)[..12];
        }
    }
}

public sealed class AccountInfo
{
    /// <summary>Esim. "Europe/Helsinki" (server_info.timezone).</summary>
    public string ServerTimeZone { get; init; } = "";
    public string Username { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime? ExpiresAt { get; init; }
    public int MaxConnections { get; init; }
    public int ActiveConnections { get; init; }
    public bool IsTrial { get; init; }
    public IReadOnlyList<string> AllowedFormats { get; init; } = [];
}

public sealed class Category
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Erikoiskategoriat (Historia, Suosikit, Kaikki) – ei Xtreamin omia.</summary>
    public SpecialCategory Special { get; init; }
    public override string ToString() => Name;
}

public enum SpecialCategory { None, History, Favorites, All, Continue }

public partial class LiveChannel : ObservableObject
{
    public int StreamId { get; init; }
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string? Logo { get; init; }
    public string? EpgChannelId { get; init; }
    public string CategoryId { get; init; } = "";
    public bool HasArchive { get; init; }
    /// <summary>Kuinka monta päivää taaksepäin jälkikatselu on saatavilla.</summary>
    public int ArchiveDays { get; init; }

    [ObservableProperty] private bool _isFavorite;

    /// <summary>Käynnissä oleva ohjelma (kanavakorteille, päivitetään oppaasta).</summary>
    [ObservableProperty] private EpgProgram? _current;
    [ObservableProperty] private double _currentProgress;

    public string NumberText => Number > 0 ? Number.ToString() : "";

    public override string ToString() => Name;
}

public partial class VodItem : ObservableObject
{
    public int StreamId { get; init; }
    public string Name { get; init; } = "";
    public string? Poster { get; init; }
    public string CategoryId { get; init; } = "";
    public string ContainerExtension { get; init; } = "mp4";
    public double Rating { get; init; }
    public DateTime? Added { get; init; }

    [ObservableProperty] private bool _isFavorite;
    /// <summary>Katselukohta 0–1 (jatka katsomista); 0 = ei aloitettu.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWatched;

    public string ProgressKey => $"movie:{StreamId}";
    public string RatingText => Rating > 0 ? Rating.ToString("0.0") : "";
    public override string ToString() => Name;
}

public sealed class VodDetails
{
    public string Plot { get; init; } = "";
    public string Cast { get; init; } = "";
    public string Director { get; init; } = "";
    public string Genre { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public string Duration { get; init; } = "";
    public string? Backdrop { get; init; }
    public string ContainerExtension { get; init; } = "";
}

public partial class SeriesItem : ObservableObject
{
    public int SeriesId { get; init; }
    public string Name { get; init; } = "";
    public string? Cover { get; init; }
    public string CategoryId { get; init; } = "";
    public string Plot { get; init; } = "";
    public string Genre { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public double Rating { get; init; }

    [ObservableProperty] private bool _isFavorite;

    public string RatingText => Rating > 0 ? Rating.ToString("0.0") : "";
    public override string ToString() => Name;
}

public sealed class SeriesDetails
{
    public string Plot { get; init; } = "";
    public string Cast { get; init; } = "";
    public string Genre { get; init; } = "";
    public string? Backdrop { get; init; }
    public IReadOnlyList<Season> Seasons { get; init; } = [];
}

public sealed class Season
{
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<Episode> Episodes { get; init; } = [];
    public override string ToString() => Name;
}

public partial class Episode : ObservableObject
{
    public string Id { get; init; } = "";
    public int Number { get; init; }
    public int Season { get; init; }
    public string Title { get; init; } = "";
    public string Plot { get; init; } = "";
    public string Duration { get; init; } = "";
    public string? Image { get; init; }
    public string ContainerExtension { get; init; } = "mp4";
    public string Label => $"{Number}. {Title}";
    public string ProgressKey => $"ep:{Id}";

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWatched;
    public override string ToString() => Label;
}

public sealed class EpgProgram
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    public bool IsNow(DateTime now) => Start <= now && now < End;
    public string TimeText => $"{Start:HH:mm} – {End:HH:mm}";
    public double Progress(DateTime now)
    {
        var total = (End - Start).TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp((now - Start).TotalSeconds / total, 0, 1);
    }
}
