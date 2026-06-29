namespace JiraUnpacked.Domain.Abstractions;

public interface ITokenProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
