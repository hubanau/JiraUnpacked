namespace JiraUnpacked.Domain.Enums;
/// <summary>
/// Cancelled is deliberately NOT Failed. Someone stopped this run on purpose; reporting that as a
/// failure would put a red row in the history for a decision that was made, and hide real
/// failures among them.
/// </summary>
public enum SyncRunStatus { Running, Succeeded, Failed, PartiallyFailed, Cancelled }
