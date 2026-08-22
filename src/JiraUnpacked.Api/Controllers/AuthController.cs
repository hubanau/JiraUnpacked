using JiraUnpacked.Api.Contracts.Auth;
using JiraUnpacked.Api.Security;
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
    private readonly IAccountDeletionService _deletion;
    private readonly IWebHostEnvironment _environment;

    public AuthController(
        IAuthService auth, IRegistrationService registration, IEmailVerificationService verification,
        IAccountDeletionService deletion, IWebHostEnvironment environment)
    {
        _auth = auth;
        _registration = registration;
        _verification = verification;
        _deletion = deletion;
        _environment = environment;
    }

    /// <summary>
    /// Writes the rotated refresh token to its httpOnly cookie and returns the body WITHOUT it.
    /// The token is deliberately absent from the response: leaving it there would let an XSS
    /// running during sign-in read it straight out, which is the exposure this replaces.
    /// </summary>
    private IActionResult IssueTokens(LoginResult result)
    {
        Response.Cookies.Append(
            RefreshTokenCookie.Name,
            result.RefreshToken!,
            RefreshTokenCookie.Options(
                _environment.IsDevelopment(), result.RefreshTokenExpiresAtUtc!.Value));

        return Ok(new TokenResponse(result.AccessToken!, result.AccessTokenExpiresAtUtc!.Value));
    }

    /// <summary>
    /// Second CSRF layer behind SameSite=Strict. Returns the response to send when the header
    /// is missing, or null when the request may proceed. 400 with an explanatory title so a
    /// non-browser caller is told what to add rather than left guessing at a 401.
    /// </summary>
    private IActionResult? RequireXhrHeader()
    {
        var header = Request.Headers[RefreshTokenCookie.RequiredHeader].ToString();
        return string.Equals(header, RefreshTokenCookie.RequiredHeaderValue, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"This endpoint requires the {RefreshTokenCookie.RequiredHeader}: "
                       + $"{RefreshTokenCookie.RequiredHeaderValue} header.");
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

        return IssueTokens(result);
    }

    /// <summary>
    /// Reads the refresh token from its cookie — there is no request body. A missing cookie is
    /// reported identically to an invalid one, so an attacker learns nothing from the difference.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (RequireXhrHeader() is { } rejected) return rejected;

        var presented = Request.Cookies[RefreshTokenCookie.Name];
        var result = string.IsNullOrEmpty(presented)
            ? new LoginResult(false, null, null, null)
            : await _auth.RefreshAsync(presented, ct);

        if (!result.Succeeded)
        {
            // Clear the cookie too: whatever the browser is holding is not usable, and leaving
            // it there means every later request re-presents a token that can only fail.
            Response.Cookies.Delete(
                RefreshTokenCookie.Name, RefreshTokenCookie.DeleteOptions(_environment.IsDevelopment()));
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid or expired refresh token.");
        }

        return IssueTokens(result);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (RequireXhrHeader() is { } rejected) return rejected;

        var presented = Request.Cookies[RefreshTokenCookie.Name];
        if (!string.IsNullOrEmpty(presented)) await _auth.LogoutAsync(presented, ct);

        // Always clear, even when no cookie was presented: 204 must mean "you are logged out",
        // never "there was nothing to do so your cookie is still there".
        Response.Cookies.Delete(
            RefreshTokenCookie.Name, RefreshTokenCookie.DeleteOptions(_environment.IsDevelopment()));
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

    [HttpDelete("me")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var claim = User.FindFirst("endUserId")?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid token.");
        }

        var result = await _deletion.DeleteAsync(userId, userId, ct);
        return result.Outcome switch
        {
            AccountDeletionOutcome.Deleted => NoContent(),
            AccountDeletionOutcome.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound, title: "User not found."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Only End User accounts can be deleted."),
        };
    }
}
