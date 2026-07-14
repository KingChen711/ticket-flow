using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Domain.Tests.Payments;

public class PaymentTransactionTests
{
    [Fact]
    public void Initiate_SetsStatusPending()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.ProcessedAt);
    }

    [Fact]
    public void MarkSucceeded_OnPendingPayment_SetsStatusAndGatewayRef()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);

        payment.MarkSucceeded("gw-ref-123", DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("gw-ref-123", payment.GatewayRef);
        Assert.NotNull(payment.ProcessedAt);
    }

    [Fact]
    public void MarkFailed_OnPendingPayment_SetsStatusAndReason()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);

        payment.MarkFailed("gateway declined", DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("gateway declined", payment.FailureReason);
    }

    [Fact]
    public void MarkSucceeded_OnAlreadySucceededPayment_Throws()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);
        payment.MarkSucceeded("gw-ref", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => payment.MarkSucceeded("gw-ref-2", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkFailed_OnAlreadyFailedPayment_Throws()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);
        payment.MarkFailed("first failure", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => payment.MarkFailed("second failure", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkRefunded_OnSucceededPayment_SetsStatusRefunded()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);
        payment.MarkSucceeded("gw-ref", DateTimeOffset.UtcNow);

        payment.MarkRefunded(DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.RefundedAt);
    }

    [Fact]
    public void MarkRefunded_OnPendingPayment_Throws()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => payment.MarkRefunded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkRefunded_OnAlreadyRefundedPayment_Throws()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);
        payment.MarkSucceeded("gw-ref", DateTimeOffset.UtcNow);
        payment.MarkRefunded(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => payment.MarkRefunded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkRefunded_OnFailedPayment_Throws()
    {
        var payment = PaymentTransaction.Initiate(Guid.NewGuid(), Money.Create(100_000), DateTimeOffset.UtcNow);
        payment.MarkFailed("declined", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => payment.MarkRefunded(DateTimeOffset.UtcNow));
    }
}
