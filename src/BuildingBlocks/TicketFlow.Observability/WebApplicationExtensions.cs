using Microsoft.AspNetCore.Builder;

namespace TicketFlow.Observability;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps `/metrics` for Prometheus to scrape (Commit 3). Anonymous by design — this repo's
    /// `AddTicketFlowAuth` only ever applies named policies per-endpoint
    /// (`.RequireAuthorization("RequireOrganizer")` etc.), never a global fallback policy, so a
    /// plain `MapPrometheusScrapingEndpoint()` with no `.RequireAuthorization()` call stays open
    /// without needing an explicit anonymous override — same as every other unauthenticated
    /// endpoint already in this codebase (health checks, `/catalog` read routes).
    /// </summary>
    public static WebApplication UseTicketFlowObservability(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();
        return app;
    }
}
