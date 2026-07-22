using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;

namespace TicketFlow.Ordering.Application.Tests;

/// <summary>
/// In-memory stand-in for MartenCheckoutOperationStore. Insert semantics are modelled honestly —
/// a duplicate id throws rather than upserting — because "the second writer loses" is the whole
/// behaviour these tests rely on.
/// </summary>
public sealed class FakeCheckoutOperationStore : ICheckoutOperationStore
{
    private readonly Dictionary<string, CheckoutOperation> _operations = [];
    private readonly Dictionary<Guid, OrderDispatch> _dispatches = [];
    private readonly List<CheckoutOperation> _stagedOperations = [];
    private readonly List<OrderDispatch> _stagedDispatches = [];

    public IReadOnlyCollection<CheckoutOperation> Operations => _operations.Values;
    public IReadOnlyCollection<OrderDispatch> Dispatches => _dispatches.Values;

    public Task<CheckoutOperation?> FindOperationAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_operations.GetValueOrDefault(key));

    public Task<CheckoutOperation?> FindOperationByHoldAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_operations.Values.SingleOrDefault(operation => operation.HoldId == holdId));

    public void StoreOperation(CheckoutOperation operation) => _stagedOperations.Add(operation);

    public void StoreDispatch(OrderDispatch dispatch) => _stagedDispatches.Add(dispatch);

    public Task<IReadOnlyList<OrderDispatch>> GetPendingDispatchesAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OrderDispatch>>(
            _dispatches.Values
                .Where(dispatch => dispatch.State == OrderDispatchState.Pending)
                .OrderBy(dispatch => dispatch.CreatedAt)
                .Take(maxCount)
                .ToList());

    public bool IsUniqueViolation(Exception exception) => exception is DuplicateCheckoutKeyException;

    public void DiscardPendingChanges()
    {
        _stagedOperations.Clear();
        _stagedDispatches.Clear();
    }

    /// <summary>Applies what the order store's SaveChangesAsync commits, in the same call.</summary>
    public void Commit()
    {
        var operations = _stagedOperations.ToList();
        var dispatches = _stagedDispatches.ToList();
        _stagedOperations.Clear();
        _stagedDispatches.Clear();

        foreach (var operation in operations)
        {
            if (_operations.ContainsKey(operation.Id) ||
                _operations.Values.Any(existing => existing.HoldId == operation.HoldId))
                throw new DuplicateCheckoutKeyException(operation.Id);

            _operations[operation.Id] = operation;
        }

        foreach (var dispatch in dispatches)
        {
            if (!_dispatches.TryAdd(dispatch.Id, dispatch))
                throw new DuplicateCheckoutKeyException(dispatch.Id.ToString());
        }
    }
}

public sealed class DuplicateCheckoutKeyException(string key)
    : Exception($"Checkout key '{key}' already exists.");

/// <summary>Ties the two fakes together so one SaveChangesAsync commits both, as Marten does.</summary>
public sealed class FakeTransactionalOrderStore(FakeOrderStore orders, FakeCheckoutOperationStore operations)
    : IOrderStore
{
    public Task<TicketFlow.Ordering.Domain.Orders.Order?> LoadAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) => orders.LoadAsync(orderId, cancellationToken);

    public void StartStream(Guid orderId, object initialEvent) => orders.StartStream(orderId, initialEvent);

    public void Append(Guid orderId, object @event) => orders.Append(orderId, @event);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        operations.Commit();
        return orders.SaveChangesAsync(cancellationToken);
    }
}
