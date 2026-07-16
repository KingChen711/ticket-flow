using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace TicketFlow.Web;

// Keycloak puts realm roles in a nested "realm_access": { "roles": [...] } claim, not a flat
// role claim — confirmed via a real token probe (Phase 7 Commit 1, docs/phase-7-plan.md mục 6).
// ClaimsPrincipal.IsInRole()/[Authorize(Roles=...)] only read ClaimTypes.Role, so this unpacks
// the nested JSON into individual role claims once per authenticated principal.
public sealed class RealmRoleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (identity.HasClaim(c => c.Type == ClaimTypes.Role))
            return Task.FromResult(principal);

        var realmAccess = identity.FindFirst("realm_access");
        if (realmAccess is null)
            return Task.FromResult(principal);

        using var document = JsonDocument.Parse(realmAccess.Value);
        if (document.RootElement.TryGetProperty("roles", out var roles))
        {
            foreach (var role in roles.EnumerateArray())
                identity.AddClaim(new Claim(ClaimTypes.Role, role.GetString()!));
        }

        return Task.FromResult(principal);
    }
}
