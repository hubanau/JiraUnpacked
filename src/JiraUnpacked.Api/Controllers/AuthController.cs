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

    public AuthController(IAuthService auth) => _auth = auth;

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

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        sub = User.FindFirst("sub")?.Value,
        role = User.FindFirst("role")?.Value,
        endUserId = User.FindFirst("endUserId")?.Value
    });
}
