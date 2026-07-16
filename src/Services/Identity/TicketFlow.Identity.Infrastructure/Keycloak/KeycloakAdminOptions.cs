namespace TicketFlow.Identity.Infrastructure.Keycloak;

public sealed record KeycloakAdminOptions(string Realm, string AdminClientId, string AdminClientSecret);
