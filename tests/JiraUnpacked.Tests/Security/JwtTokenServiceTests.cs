using System.Security.Cryptography;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace JiraUnpacked.Tests.Security;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService(FakeClock clock)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "test-iss",
            Audience = "test-aud",
            SigningKey = "this-is-a-32-byte-minimum-test-key!!",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });
        return new JwtTokenService(options, clock);
    }

    private static User SampleUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "a@b.com",
        Username = "alice",
        PasswordHash = "x",
        Role = UserRole.EndUser,
        Status = UserStatus.Active
    };

    [Fact]
    public void CreateAccessToken_EmbedsClaims_AndExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = CreateService(new FakeClock(now));
        var user = SampleUser();

        var token = service.CreateAccessToken(user);

        Assert.Equal(now.AddMinutes(15), token.ExpiresAtUtc);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Token);
        Assert.Equal(user.Id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal("EndUser", jwt.GetClaim("role").Value);
        Assert.Equal(user.Id.ToString(), jwt.GetClaim("endUserId").Value);
    }

    [Fact]
    public void CreateRefreshToken_ReturnsRawWithMatchingHashAndExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = CreateService(new FakeClock(now));

        var refresh = service.CreateRefreshToken();

        Assert.False(string.IsNullOrWhiteSpace(refresh.Token));
        Assert.Equal(now.AddDays(7), refresh.ExpiresAtUtc);
        Assert.Equal(service.HashRefreshToken(refresh.Token), refresh.TokenHash);
        Assert.NotEqual(refresh.Token, refresh.TokenHash);
    }
}
