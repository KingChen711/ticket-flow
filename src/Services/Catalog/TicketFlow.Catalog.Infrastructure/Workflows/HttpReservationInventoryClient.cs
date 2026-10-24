using System.Net;
using System.Net.Http.Json;
using TicketFlow.Catalog.Application.Abstractions;

namespace TicketFlow.Catalog.Infrastructure.Workflows;

public sealed class HttpReservationInventoryClient(HttpClient client) : IReservationInventoryClient
{
    public async Task ProvisionAsync(
        Guid eventId,
        IReadOnlyList<ReservationInventorySection> sections,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync(
            $"events/{eventId}/inventory",
            new { sections },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CleanupAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        using var response = await client.DeleteAsync($"events/{eventId}/inventory", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }
}
