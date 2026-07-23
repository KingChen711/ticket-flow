namespace TicketFlow.Search.Infrastructure;

/// <summary>
/// Raised when Elasticsearch answers with anything other than a genuine result — an outage, a 429
/// rejecting the request under load, a 5xx (Commit 11).
/// </summary>
/// <remarks>
/// Before this, every method here returned whatever the client handed back without checking
/// <c>IsValidResponse</c> first. A search failure surfaced as <c>Total == 0</c>, indistinguishable
/// from a genuinely empty result set — a customer during an ES outage would see "no events found"
/// instead of an error, and a consumer applying a lifecycle event would treat a failed write as a
/// silent success and never retry it. Throwing here instead lets a consumer's exception reach
/// MassTransit's retry/DLQ pipeline, and lets an HTTP caller receive a 5xx instead of an empty page.
/// </remarks>
public sealed class SearchIndexUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
