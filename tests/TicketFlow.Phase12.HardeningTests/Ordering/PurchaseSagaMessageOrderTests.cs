using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Ordering;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class PurchaseSagaMessageOrderTests(HardeningEnvironmentFixture environment)
{
    public static IEnumerable<object[]> ParticipantMilestonePermutations()
    {
        var milestones = new[] { "SeatsConfirmed", "PaymentProcessed", "TicketsIssued" };
        foreach (var first in milestones)
        foreach (var second in milestones.Where(item => item != first))
        foreach (var third in milestones.Where(item => item != first && item != second))
            yield return [new[] { first, second, third }];
    }

    [Theory]
    [MemberData(nameof(ParticipantMilestonePermutations))]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Every_participant_milestone_permutation_and_duplicate_must_converge_once(string[] deliveryOrder)
    {
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await environment.PublishEndpoint.Publish(new OrderSubmitted(
            orderId,
            eventId,
            userId,
            holdId,
            [seatId],
            500_000m,
            "CreditCard",
            "Buyer",
            "buyer@example.com",
            now));

        Assert.True(await AsyncTest.WaitUntilAsync(
            () => environment.Messages.Count<ConfirmSeats>(message => message.OrderId == orderId) == 1,
            TimeSpan.FromSeconds(10)),
            "The real RabbitMQ/Marten saga did not enter AwaitingSeats.");

        foreach (var milestone in deliveryOrder)
        {
            // Each business fact uses two distinct RabbitMQ envelopes. MessageId inbox dedup
            // cannot help; the process manager must make the transition itself idempotent.
            await PublishMilestoneAsync(milestone, orderId, holdId, seatId, paymentId, now);
            await PublishMilestoneAsync(milestone, orderId, holdId, seatId, paymentId, now);
        }

        var completed = await AsyncTest.WaitUntilAsync(
            () => environment.Messages.Count<OrderCompleted>(message => message.OrderId == orderId) >= 1,
            TimeSpan.FromSeconds(3));

        Assert.True(completed,
            $"Delivery [{string.Join(", ", deliveryOrder)}] left the saga stuck because early milestones were discarded.");
        Assert.Equal(1, environment.Messages.Count<OrderCompleted>(message => message.OrderId == orderId));
    }

    /// <summary>
    /// Only <c>Initially</c> creates an instance, so a participant answer that overtakes
    /// <c>OrderSubmitted</c> — the two are published independently and RabbitMQ orders nothing
    /// between exchanges — used to match no correlation and be discarded, leaving the purchase
    /// waiting forever on a milestone that had already happened. Commit 9 schedules it back instead.
    /// </summary>
    /// <remarks>
    /// Deliberately on the real broker: the redelivery is done by the RabbitMQ delayed-message
    /// scheduler, which the in-memory harness does not have, so an in-memory version of this test
    /// would prove nothing about the mechanism actually shipping.
    /// </remarks>
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_milestone_arriving_before_the_saga_exists_must_be_redelivered_not_dropped()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await environment.PublishEndpoint.Publish(new SeatsConfirmed(orderId, [seatId], now));
        await environment.PublishEndpoint.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [seatId], 500_000m,
            "CreditCard", "Buyer", "buyer@example.com", now));

        // The dropped SeatsConfirmed used to leave the saga stuck in AwaitingSeats forever; the
        // proof it was not dropped is that the saga moves on to asking for payment.
        Assert.True(
            await AsyncTest.WaitUntilAsync(
                () => environment.Messages.Count<ProcessPayment>(message => message.OrderId == orderId) == 1,
                TimeSpan.FromSeconds(30)),
            "The early SeatsConfirmed was dropped instead of being redelivered.");
    }

    private Task PublishMilestoneAsync(
        string milestone,
        Guid orderId,
        Guid holdId,
        Guid seatId,
        Guid paymentId,
        DateTimeOffset now) =>
        milestone switch
        {
            "SeatsConfirmed" => environment.PublishEndpoint.Publish(
                new SeatsConfirmed(orderId, [seatId], now)),
            "PaymentProcessed" => environment.PublishEndpoint.Publish(
                new PaymentProcessed(orderId, paymentId, 500_000m, now)),
            "TicketsIssued" => environment.PublishEndpoint.Publish(
                new TicketsIssued(orderId, [$"{orderId}/{seatId}.pdf"], now)),
            _ => throw new ArgumentOutOfRangeException(nameof(milestone), milestone, "Unknown milestone."),
        };
}
