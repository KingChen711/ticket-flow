using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>
/// Real pipeline (Commit 7): charge via the Polly-wrapped gateway, persist, publish the result.
/// Two idempotency layers, as called for in phase-5-plan.md architecture decision 6:
/// (1) business-key dedup by OrderId here (GetByOrderIdAsync before charging — a second physically
/// distinct message for the same order re-publishes the already-decided result instead of
/// charging again); (2) MassTransit EF Core Inbox on this consumer's endpoint (wired in
/// Program.cs via UseEntityFrameworkOutbox) — catches the OTHER duplicate scenario, the same
/// physical message (same MessageId) redelivered after a crash-before-ack, which OrderId dedup
/// alone wouldn't need to handle since it'd never even reach here twice.
/// </summary>
public sealed class ProcessPaymentConsumer(
    IPaymentRepository repository,
    IPaymentGatewayClient gatewayClient,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ProcessPaymentConsumer> logger) : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(ProcessPayment), message.PriceSnapshotVersion);

        var existing = await repository.GetByOrderIdAsync(message.OrderId, context.CancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "[payment] duplicate ProcessPayment for order {OrderId} — replaying existing result ({Status}), not charging again.",
                message.OrderId,
                existing.Status);

            await RepublishResultAsync(context, existing);
            return;
        }

        var payment = PaymentTransaction.Initiate(message.OrderId, Money.Create(message.Amount), timeProvider.GetUtcNow());
        repository.Add(payment);

        var result = await gatewayClient.ChargeAsync(message.OrderId, message.Amount, message.PaymentMethod, context.CancellationToken);

        var now = timeProvider.GetUtcNow();
        if (result.Succeeded)
            payment.MarkSucceeded(result.GatewayRef!, now);
        else
            payment.MarkFailed(result.FailureReason ?? "Gateway declined the charge.", now);

        await unitOfWork.SaveChangesAsync(context.CancellationToken);
        await RepublishResultAsync(context, payment);
    }

    private static Task RepublishResultAsync(ConsumeContext<ProcessPayment> context, PaymentTransaction payment) =>
        payment.Status switch
        {
            PaymentStatus.Succeeded => context.Publish(new PaymentProcessed(
                payment.OrderId, payment.Id, payment.Amount.Amount, payment.ProcessedAt!.Value)),
            PaymentStatus.Failed => context.Publish(new PaymentFailed(payment.OrderId, payment.FailureReason!)),
            _ => throw new InvalidOperationException($"Unexpected payment status '{payment.Status}' for order {payment.OrderId}."),
        };
}
