namespace JiraUnpacked.Domain.Abstractions;

/// <summary>Result of deleting a connection or a project.</summary>
public enum DeleteOutcome
{
    Deleted,

    /// <summary>Absent, or not the caller's — the two are deliberately indistinguishable.</summary>
    NotFound,

    /// <summary>A sync is Running for one of the affected projects; stop it (or let it finish) first.</summary>
    SyncRunning
}
