using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TicketFlow.Identity.Application.Users;

namespace TicketFlow.Identity.Api.Security;

/// <summary>
/// Dev-only token issuance — signs with a static symmetric key from config, no refresh
/// tokens, no revocation. This whole class gets thrown away in Phase 7 when real OAuth2/OIDC
/// (Keycloak/Duende IdentityServer) replaces it; nothing here should grow real auth features.
/// </summary>
public sealed class DevTokenGenerator(IConfiguration configuration)
{
    public (string AccessToken, DateTimeOffset ExpiresAt) GenerateToken(UserResponse user)
    {
        var jwtSection = configuration.GetSection("Jwt");
        var signingKey = jwtSection["SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        var issuer = jwtSection["Issuer"] ?? "ticketflow-identity-dev";
        var audience = jwtSection["Audience"] ?? "ticketflow-dev";
        var expiresMinutes = jwtSection.GetValue<int?>("ExpiresMinutes") ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(expiresMinutes);

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            .. user.Roles.Select(role => new Claim("role", role)),
        ];

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
