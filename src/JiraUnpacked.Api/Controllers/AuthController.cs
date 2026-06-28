using JiraUnpacked.Api.Contracts.Auth;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IRegistrationService _registration;
    private readonly IEmailVerificationService _verification;

    public AuthController(
        IAuthService auth, IRegistrationService registration, IEmailVerificationService verification)
    {
        _auth = auth;
        _registration = registration;
        _verification = verification;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await _registration.RegisterAsync(
            request.InviteCode, request.Email, request.Username, request.Password, ct);

        return result.Error switch
        {
            RegistrationError.None => Ok(new { message = "Registration received. Check your email for a verification code." }),
            RegistrationError.InvalidInviteCode => Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid, expired, or already-used invite code."),
            RegistrationError.WeakPassword => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Password does not meet the strength policy.",
                detail: string.Join(" ", result.Errors)),
            RegistrationError.EmailOrUsernameTaken => Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Email or username is already in use."),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    [HttpPost("verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Verify(VerifyRequest request, CancellationToken ct)
    {
        var result = await _verification.VerifyAsync(request.Email, request.Code, ct);
        return result.Error switch
        {
            VerificationError.None => Ok(new { message = "Email verified. You can now sign in." }),
            VerificationError.TooManyAttempts => Problem(
                statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts. Request a new code."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid or expired verification code.")
        };
    }

    [HttpPost("verify/resend")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendVerification(ResendVerificationRequest request, CancellationToken ct)
    {
        await _verification.ResendAsync(request.Email, ct);
        // Always 202 — never reveal whether the account exists or is pending.
        return Accepted(new { message = "If the account is awaiting verification, a new code has been sent." });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request.EmailOrUsername, request.Password, ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");
        }

        return Ok(new TokenResponse(
            result.AccessToken!, result.AccessTokenExpiresAtUtc!.Value, result.RefreshToken!));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(request.RefreshToken, ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid or expired refresh token.");
        }

        return Ok(new TokenResponse(
            result.AccessToken!, result.AccessTokenExpiresAtUtc!.Value, result.RefreshToken!));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        await _auth.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        sub = User.FindFirst("sub")?.Value,
        role = User.FindFirst("role")?.Value,
        endUserId = User.FindFirst("endUserId")?.Value
    });
}
