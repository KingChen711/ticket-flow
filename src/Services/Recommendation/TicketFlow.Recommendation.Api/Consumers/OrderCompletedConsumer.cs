using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Recommendation.Application.Abstractions;

namespace TicketFlow.Recommendation.Api.Consumers;

/// <summary>
/// OrderCompleted only carries EventId (Ordering's saga pipeline has never had Artist/VenueName
/// anywhere) — deliberately not enriched for this consumer's sake. Commit 4's recommendation
/// query looks up Artist/VenueName from Search's index by EventId at read time instead.
/// Idempotent by construction: RecordPurchaseAsync uses MongoDB's $addToSet, so a redelivered
/// OrderCompleted for the same (UserId, EventId) does not duplicate the EventId in the array.
/// </summary>
public sealed class OrderCompletedConsumer(IPurchaseHistoryStore store) : IConsumer<OrderCompleted>
{
    public Task Consume(ConsumeContext<OrderCompleted> context)
    {
        var message = context.Message;
        return store.RecordPurchaseAsync(message.UserId, message.EventId, message.CompletedAt, context.CancellationToken);
    }
}
