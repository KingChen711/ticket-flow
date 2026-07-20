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
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
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
