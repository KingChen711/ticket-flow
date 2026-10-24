using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketFlow.Identity.Domain.Users;
using TicketFlow.Identity.Infrastructure.Keycloak;

namespace TicketFlow.Phase12.HardeningTests.Identity;

public sealed class KeycloakAdminClientContractTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Created_user_requires_email_verification_and_can_be_recovered_by_exact_email()
    {
        var userId = Guid.NewGuid();
        var handler = new KeycloakStubHandler(userId);
        var registrationKey = Guid.NewGuid();
        var client = new KeycloakAdminClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://keycloak") },
            new KeycloakAdminOptions("ticketflow", "admin-client", "secret"));

        handler.RegistrationKey = registrationKey;
        var created = await client.CreateUserAsync("buyer@example.com", "Buyer", registrationKey);
        await client.ConfigureUserAsync(created, "Str0ng!TicketFlow", Role.Customer);
        var recovered = await client.FindUserByNormalizedEmailAsync("buyer@example.com", registrationKey);
        var unrelated = await client.FindUserByNormalizedEmailAsync("buyer@example.com", Guid.NewGuid());
        await client.DeleteUserAsync(created); // 404 is an idempotent successful cleanup.

        Assert.Equal(userId, created);
        Assert.Equal(userId, recovered);
        Assert.Null(unrelated);

        using var payload = JsonDocument.Parse(handler.CreatePayload!);
        var root = payload.RootElement;
        Assert.False(root.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("VERIFY_EMAIL", root.GetProperty("requiredActions")[0].GetString());
        Assert.Equal("buyer@example.com", root.GetProperty("username").GetString());
        Assert.Equal("buyer@example.com", root.GetProperty("email").GetString());
        Assert.Equal(
            registrationKey.ToString("D"),
            root.GetProperty("attributes").GetProperty("ticketflowRegistrationKey")[0].GetString());

        Assert.Contains(handler.Paths, path => path.Contains("username=buyer%40example.com", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("email=buyer%40example.com", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.EndsWith("/reset-password", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    private sealed class KeycloakStubHandler(Guid userId) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? CreatePayload { get; private set; }
        public Guid RegistrationKey { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);

            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
                return Json(new { access_token = "admin-token" });

            if (request.Method == HttpMethod.Post && path.EndsWith("/users", StringComparison.Ordinal))
            {
                CreatePayload = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Location = new Uri($"http://keycloak/admin/realms/ticketflow/users/{userId}") },
                };
            }

            if (request.Method == HttpMethod.Get && path.Contains("/users?", StringComparison.Ordinal))
            {
                return Json(new[]
                {
                    new
                    {
                        id = userId,
                        username = "buyer@example.com",
                        email = "buyer@example.com",
                        attributes = new Dictionary<string, string[]>
                        {
                            ["ticketflowRegistrationKey"] = [RegistrationKey.ToString("D")],
                        },
                    },
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/roles/customer", StringComparison.Ordinal))
                return Json(new { id = Guid.NewGuid(), name = "customer" });

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
