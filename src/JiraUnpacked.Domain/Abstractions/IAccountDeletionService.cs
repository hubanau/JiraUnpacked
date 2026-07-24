namespace JiraUnpacked.Domain.Abstractions;

public enum AccountDeletionOutcome { Deleted, NotFound, NotAnEndUser }

public sealed record AccountDeletionResult(AccountDeletionOutcome Outcome);

/// <summary>Irreversibly erases an End User account and every row of tenant data it owns.</summary>
public interface IAccountDeletionService
{
    Task<AccountDeletionResult> DeleteAsync(Guid userId, Guid requestedBy, CancellationToken ct = default);
}
