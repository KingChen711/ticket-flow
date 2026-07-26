using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace TicketFlow.Web;

public static class AuthenticationExtensions
{
    // Only the services that own a real role-based business rule call this (catalog writes,
    // analytics revenue, ordering, identity users, gateway) — see docs/phase-7-plan.md mục 3.
    // Authority/ValidIssuers split is the real fix found by Phase 7 Commit 1's probe: the
    // Docker-internal hostname (keycloak:8180) is needed to fetch JWKS from inside the network,
    // but tokens issued via the host-facing URL (localhost:8180) carry that as their iss claim —
    // a naive Authority-equals-issuer setup 401s valid tokens.
    public static IServiceCollection AddTicketFlowAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var keycloak = configuration.GetSection("Keycloak");
        var authority = keycloak["Authority"]
            ?? throw new InvalidOperationException("Missing configuration: Keycloak:Authority");
        var audience = keycloak["Audience"]
            ?? throw new InvalidOperationException("Missing configuration: Keycloak:Audience");
        var validIssuers = keycloak.GetSection("ValidIssuers").Get<string[]>();
        if (validIssuers is null || validIssuers.Length == 0)
            throw new InvalidOperationException("Missing configuration: Keycloak:ValidIssuers");
        var production = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);
        if (production && (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
                           authorityUri.Scheme != Uri.UriSchemeHttps ||
                           validIssuers.Any(issuer =>
                               !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
                               issuerUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                "Production Keycloak authority and every valid issuer must use HTTPS.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = production;
                options.MapInboundClaims = false; // giữ tên claim gốc (email/sub/name) — mặc định remap sang URI kiểu cũ (xem mục 6 phase-7-plan.md)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuers = validIssuers,
                    ValidateIssuer = true,
                    ValidAudience = audience,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                };

                // SignalR's WebSocket transport can't set an Authorization header during the
                // handshake — its JS client instead appends ?access_token=... to the connection
                // URL (Phase 8 Commit 6). Harmless fallback for everything else: only kicks in
                // when the query string actually carries the token AND the path is a hub path.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.Value?.Contains("/hubs/") == true)
                            context.Token = accessToken;

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSingleton<IClaimsTransformation, RealmRoleClaimsTransformation>();

        services.AddAuthorizationBuilder()
            .AddPolicy("RequireOrganizer", p => p.RequireRole("organizer", "admin"))
            .AddPolicy("RequireAdmin", p => p.RequireRole("admin"))
            .AddPolicy("RequireAuthenticated", p => p.RequireAuthenticatedUser())
            // Commit 14: endpoints one service calls on another, never a browser. Before this they
            // were simply unauthenticated and relied on "only our containers can reach this port"
            // — the exact "tin Docker network" the plan rules out as a security boundary (quyết
            // định 8). The `service` realm role is deliberately NOT granted to any human-facing
            // client, so a customer or organizer token can never satisfy this policy no matter how
            // it was obtained; admin is not an escape hatch here either, because these routes take
            // an already-verified identity as a parameter and would let a human impersonate one.
            .AddPolicy("RequireServicePrincipal", p => p.RequireRole("service"));

        return services;
    }
}
