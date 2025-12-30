namespace PlexRequest;

public sealed class SheetRow
{
    // A TITLE
    public string Title { get; init; } = "";
    // B TYPE
    public string Type { get; init; } = "";

    // C ID (required)
    //   TV/ANIME => TVDB
    //   MOVIE    => TMDB
    public string IdRaw { get; init; } = "";
    public int? Id { get; init; }

    // Bookkeeping
    public int SheetRowNumber { get; init; } // e.g., 3, 4, 5...
}
