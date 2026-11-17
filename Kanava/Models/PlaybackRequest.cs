namespace Kanava.Models;

/// <summary>Elokuvan, jakson tai jälkikatseluohjelman toistopyyntö VOD-soittimelle.</summary>
public sealed class PlaybackRequest
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    /// <summary>Katselukohdan avain ("movie:123", "ep:456"); null = ei tallenneta (esim. jälkikatselu).</summary>
    public string? ProgressKey { get; init; }
    /// <summary>Äänen viiveen avain; oletuksena ProgressKey.</summary>
    public string? SyncKey { get; init; }
    /// <summary>Aloituskohta (jatka katsomista).</summary>
    public long ResumeMs { get; init; }
    public int? SeriesId { get; init; }
    public int Season { get; init; }
    public int Episode { get; init; }
    /// <summary>Seuraava jakso (sarjat) – soitin tarjoaa sitä lopussa.</summary>
    public Func<PlaybackRequest?>? Next { get; init; }
    /// <summary>Lyhyt kuvaus seuraavasta, esim. "K1 J4 Pilotti".</summary>
    public string? NextLabel { get; init; }
}
