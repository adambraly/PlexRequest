namespace PlexRequest;

public enum RequestAction
{
    None,
    Added,
    Updated,
    Completed,
    Stale,
    NeedsId,
    IdMismatch,
    BadType
}
