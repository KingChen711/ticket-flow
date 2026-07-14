using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// Hand-rolled instead of pulling in Marten.AspNetCore — that package's real purpose is request
/// middleware helpers (serialize a document straight to the HTTP response), not specifically
/// health checks; adding a whole package for one liveness probe would be the same mistake as
/// pulling ASP.NET Core Identity just for PasswordHasher (Phase 1).
///
/// Deliberately queries the EVENT STORE (session.Events.QueryAllRawEvents), not
/// session.Query&lt;Order&gt;() — a real bug caught during verification: calling
/// session.Query&lt;Order&gt;() makes Marten treat Order as a plain DOCUMENT type and silently
/// creates an mt_doc_order table for it, which has nothing to do with the event-sourced stream
/// this service actually relies on (mt_events/mt_streams). Order must never be queried as a
/// document — only ever reconstructed by folding its event stream.
/// </summary>
public sealed class MartenHealthCheck(IQuerySession session) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await session.Events.QueryAllRawEvents().Take(1).ToListAsync(cancellationToken);
            return HealthCheckResult.Healthy("Marten event store query succeeded.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Marten event store query failed.", ex);
        }
    }
}
