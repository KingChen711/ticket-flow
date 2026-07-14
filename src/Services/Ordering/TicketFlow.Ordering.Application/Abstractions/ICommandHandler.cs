namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// The whole "dispatcher" — a naming convention, not a runtime mediator. Endpoints inject the
/// concrete handler type directly (e.g. PlaceOrderCommandHandler), so there's no ISender/Send()
/// indirection resolving handlers by type at runtime. This gives CQRS's real teaching point
/// (explicit Command type + single-purpose Handler type, separate from the Query side in
/// Commit 6) without MediatR's machinery — see docs/phase-4-plan.md quyết định kiến trúc mục 3
/// for why: MediatR v13+ went commercial, and its real value (pipeline behaviors) isn't needed
/// by this phase's DoD.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
