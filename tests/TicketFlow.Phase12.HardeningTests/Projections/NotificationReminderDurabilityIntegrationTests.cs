using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TicketFlow.Notification.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Projections;

/// <summary>
/// Same purpose as SearchIndexDurabilityIntegrationTests: proves the revision/tombstone decision
/// holds against real MongoDB, specifically the compare-and-swap-via-duplicate-key pattern
/// MongoEventReminderReadModel relies on — a filter mistake there would either silently accept
/// out-of-order writes or throw on every legitimate one, and no in-memory fake would catch it.
/// </summary>
[Collection(HardeningEnvironmentCollection.Name)]
public sealed class NotificationReminderDurabilityIntegrationTests(HardeningEnvironmentFixture environment)
{
    private MongoEventReminderReadModel ReadModel => environment.ReminderReadModel;

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_cancelled_event_must_never_be_resurrected_by_a_late_Published()
    {
        var eventId = Guid.NewGuid();

        await ReadModel.ApplyPublishedAsync(
            eventId, "Concert", "Artist", "Venue", DateTimeOffset.UtcNow.AddDays(5), revision: 1);
        await ReadModel.ApplyCancelledAsync(eventId, revision: 2);

        // A stale Published (revision 1) arrives after the Cancel (revision 2) — the tombstone
        // must win regardless of revision, because Catalog's domain forbids un-cancelling.
        await ReadModel.ApplyPublishedAsync(
            eventId, "Concert", "Artist", "Venue", DateTimeOffset.UtcNow.AddDays(5), revision: 1);

        // GetPendingRemindersAsync's own query excludes cancelled events — that's the observable
        // proof from the public port: the event must not surface even inside a wide window.
        var pending = await ReadModel.GetPendingRemindersAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(365));
        Assert.DoesNotContain(pending, reminder => reminder.CatalogEventId == eventId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_applies_for_one_event_must_not_throw_and_must_converge()
    {
        var eventId = Guid.NewGuid();
        var startsAt = DateTimeOffset.UtcNow.AddDays(10);

        // Ten concurrent upserts at the SAME revision — the duplicate-key race is the mechanism
        // under test: every loser must be caught and swallowed as a no-op, not surfaced as an
        // unhandled exception that would fault the consumer.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ReadModel.ApplyPublishedAsync(
            eventId, "Concert", "Artist", "Venue", startsAt, revision: 1)));

        // Scoped to this event's id. GetPendingRemindersAsync queries the whole collection, which
        // every test in this class shares, so an unscoped Assert.Empty here asserts something
        // about the other tests rather than about this one. No recipient was added for this event,
        // so nothing of its own is due — what this proves is that the ten concurrent applies threw
        // nothing and left one document behind, not zero and not ten.
        var pending = await ReadModel.GetPendingRemindersAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(365));
        Assert.DoesNotContain(pending, reminder => reminder.CatalogEventId == eventId);

        // Read back as BsonDocument, not through a mapped class: the point is what the ten writers
        // actually left in the database, and a class map would quietly normalise it.
        var stored = await environment.ReminderDatabase
            .GetCollection<BsonDocument>("eventSchedules")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", eventId))
            .ToListAsync();
        var document = Assert.Single(stored);
        Assert.Equal(1L, document["Revision"].ToInt64());
        Assert.Equal(startsAt.UtcDateTime, document["StartsAt"].ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_Rescheduled_that_overtakes_Published_must_create_the_document_not_lose_the_fact()
    {
        var eventId = Guid.NewGuid();
        var newStart = DateTimeOffset.UtcNow.AddDays(1);
        var orderId = Guid.NewGuid();

        // No Published has arrived for this event yet — the old UpdateEventStartsAtAsync had no
        // document to patch and silently dropped the reschedule forever.
        await ReadModel.ApplyRescheduledAsync(eventId, newStart, revision: 1);
        await ReadModel.AddRecipientAsync(orderId, eventId, Guid.NewGuid(), "Buyer", "buyer@example.com");

        var pending = await ReadModel.GetPendingRemindersAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2));
        Assert.Contains(pending, reminder => reminder.CatalogEventId == eventId && reminder.OrderId == orderId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_document_written_before_Commit_11_must_still_accept_a_revisioned_write()
    {
        var eventId = Guid.NewGuid();
        var realStart = DateTimeOffset.UtcNow.AddDays(5);
        var orderId = Guid.NewGuid();

        // The shape a pre-Commit-11 write left in a live collection: no Revision field at all.
        // Inserted through a POCO that simply lacks the property, rather than a hand-built
        // BsonDocument, so the driver's own _id/Guid serialization is used — a hand-rolled _id
        // would test nothing but my ability to guess the driver's binary Guid encoding.
        await environment.ReminderDatabase
            .GetCollection<LegacyScheduleDocument>("eventSchedules")
            .InsertOneAsync(new LegacyScheduleDocument
            {
                Id = eventId,
                Title = "Legacy",
                Artist = "Artist",
                VenueName = "Venue",
                StartsAt = DateTimeOffset.UtcNow.AddDays(400).UtcDateTime,
                IsCancelled = false,
            });

        await ReadModel.ApplyPublishedAsync(eventId, "Upgraded", "Artist", "Venue", realStart, revision: 3);
        await ReadModel.AddRecipientAsync(orderId, eventId, Guid.NewGuid(), "Buyer", "buyer@example.com");

        var pending = await ReadModel.GetPendingRemindersAsync(DateTimeOffset.UtcNow, realStart.AddDays(1));
        var reminder = Assert.Single(pending, r => r.CatalogEventId == eventId);
        Assert.Equal("Upgraded", reminder.EventTitle);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_old_v1_message_at_revision_zero_must_never_overwrite_a_v2_document()
    {
        var eventId = Guid.NewGuid();
        var realStart = DateTimeOffset.UtcNow.AddDays(5);
        var orderId = Guid.NewGuid();

        await ReadModel.ApplyPublishedAsync(eventId, "Real Title", "Real Artist", "Real Venue", realStart, revision: 3);
        // A v1 producer that never learned about AggregateVersion always sends 0.
        await ReadModel.ApplyPublishedAsync(
            eventId, "Stale v1 Title", "", "", DateTimeOffset.UtcNow.AddDays(999), revision: 0);
        await ReadModel.AddRecipientAsync(orderId, eventId, Guid.NewGuid(), "Buyer", "buyer@example.com");

        var pending = await ReadModel.GetPendingRemindersAsync(
            DateTimeOffset.UtcNow, realStart.AddDays(1));
        var reminder = Assert.Single(pending, r => r.CatalogEventId == eventId);
        Assert.Equal("Real Title", reminder.EventTitle);
        // Millisecond tolerance because that is BSON's actual resolution — a UTC datetime is
        // stored as milliseconds since the epoch, so the sub-millisecond ticks in a
        // DateTimeOffset.UtcNow are dropped by the database, not by the code under test.
        Assert.Equal(realStart, reminder.StartsAt, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// MongoEventReminderReadModel's own document class, minus the Revision property Commit 11
    /// added — i.e. exactly what is already sitting in a collection written by the deployed
    /// version.
    /// </summary>
    private sealed class LegacyScheduleDocument
    {
        [BsonId]
        public Guid Id { get; set; }

        public string Title { get; set; } = null!;
        public string Artist { get; set; } = null!;
        public string VenueName { get; set; } = null!;
        public DateTime StartsAt { get; set; }
        public bool IsCancelled { get; set; }
    }
}
