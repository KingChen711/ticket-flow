using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Api.Consumers;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Payment;

public sealed class PaymentSemanticDuplicateTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Distinct_messages_for_one_OrderId_must_execute_exactly_one_provider_charge()
    {
        var repository = new TransactionalPaymentRepositoryFake();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => new GatewayChargeResult(true, $"gw-{Guid.NewGuid():N}", null),
            rendezvousParticipants: 2);
        var gateway = new ControlledPaymentGatewayFake(sideEffect);

        var unitOfWork = new TransactionalPaymentUnitOfWorkFake(repository);
        var bus = Bus.Factory.CreateUsingInMemory(configuration =>
        {
            configuration.ReceiveEndpoint("phase12-payment-a", endpoint => endpoint.Consumer(() =>
                new ProcessPaymentConsumer(
                    repository, gateway, unitOfWork, TimeProvider.System,
                    NullLogger<ProcessPaymentConsumer>.Instance)));
            configuration.ReceiveEndpoint("phase12-payment-b", endpoint => endpoint.Consumer(() =>
                new ProcessPaymentConsumer(
                    repository, gateway, unitOfWork, TimeProvider.System,
                    NullLogger<ProcessPaymentConsumer>.Instance)));
        });

        await bus.StartAsync();

        try
        {
            var orderId = Guid.NewGuid();
            var message = new ProcessPayment(orderId, Guid.NewGuid(), 500_000m, "CreditCard");
            var endpointA = await bus.GetSendEndpoint(new Uri("loopback://localhost/phase12-payment-a"));
            var endpointB = await bus.GetSendEndpoint(new Uri("loopback://localhost/phase12-payment-b"));

            // Two independent endpoints force concurrent execution of the real consumer while
            // sharing the same datastore/provider boundary. Each envelope has a distinct id.
            await Task.WhenAll(
                endpointA.Send(message, context => context.MessageId = Guid.NewGuid()),
                endpointB.Send(message, context => context.MessageId = Guid.NewGuid()));

            Assert.True(await sideEffect.WaitForSuccessfulSideEffectsAsync(2, TimeSpan.FromSeconds(10)),
                "Both semantic duplicates did not reach the controlled provider barrier.");

            // Desired business-key claim: one PaymentAttempt row must exist before the provider call.
            Assert.Equal(1, sideEffect.SuccessfulSideEffects);
        }
        finally
        {
            await bus.StopAsync();
        }
    }
}

internal sealed class TransactionalPaymentRepositoryFake : IPaymentRepository
{
    private readonly ConcurrentDictionary<Guid, PaymentTransaction> _committedByOrderId = [];
    private readonly AsyncLocal<PaymentTransaction?> _pending = new();

    public Task<PaymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_committedByOrderId.Values.SingleOrDefault(payment => payment.Id == id));

    public Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_committedByOrderId.GetValueOrDefault(orderId));

    public void Add(PaymentTransaction payment) => _pending.Value = payment;

    public void Commit()
    {
        var payment = _pending.Value ?? throw new InvalidOperationException("No staged payment for this consume scope.");
        _committedByOrderId.TryAdd(payment.OrderId, payment);
        _pending.Value = null;
    }
}

internal sealed class TransactionalPaymentUnitOfWorkFake(TransactionalPaymentRepositoryFake repository)
    : TicketFlow.Payment.Application.Abstractions.IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        repository.Commit();
        return Task.CompletedTask;
    }
}
