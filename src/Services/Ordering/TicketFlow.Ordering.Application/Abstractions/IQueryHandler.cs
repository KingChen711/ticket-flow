namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Read-side counterpart to ICommandHandler — same convention-only shape, no runtime dispatch.
/// Handlers here read MyOrderView (the projection, Commit 5) through IOrderViewStore; they never
/// touch IOrderStore or fold an event stream — that's the actual CQRS split this commit exists
/// to demonstrate (write model vs read model, not just "two folders").
/// </summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
