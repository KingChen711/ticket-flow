using Microsoft.EntityFrameworkCore;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure;

public sealed class PaymentRepository(PaymentDbContext dbContext) : IPaymentRepository
{
    public Task<PaymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PaymentTransactions.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.PaymentTransactions.FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);

    public void Add(PaymentTransaction payment) => dbContext.PaymentTransactions.Add(payment);
}
