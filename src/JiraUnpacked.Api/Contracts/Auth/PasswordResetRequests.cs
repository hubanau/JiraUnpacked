namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);
