using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Infrastructure;

/// <summary>
/// Two Mongo collections behind one port (see IEventReminderReadModel's doc comment): schedules
/// keyed by CatalogEventId (Published/Rescheduled/Cancelled all target one doc each), recipients
/// keyed by OrderId (one reminder per order, not per seat). GetPendingRemindersAsync joins them
/// in application code rather than a Mongo aggregation pipeline — both collections are small
/// (event count and order count for a learning project, not production scale), so a two-query
/// join is simpler to read and debug than an aggregation stage.
///
/// Dates stored as plain UTC DateTime, not DateTimeOffset: the BSON driver's default
/// DateTimeOffsetSerializer represents the value as a 2-element [ticks, offsetMinutes] array, not
/// a native BSON date — $gte/$lte range queries against that shape aren't a documented-correct
/// comparison. Every timestamp in this codebase's contracts is already UTC in practice
/// (DateTimeOffset.UtcNow everywhere), so converting to DateTime at the write boundary and back
/// to DateTimeOffset (UTC) at the read boundary loses nothing and gets BSON's native, properly
/// sortable date type instead.
/// </summary>
public sealed class MongoEventReminderReadModel(IMongoDatabase database) : IEventReminderReadModel
{
    private const string SchedulesCollectionName = "eventSchedules";
    private const string RecipientsCollectionName = "reminderRecipients";

    private IMongoCollection<EventScheduleDocument> Schedules => database.GetCollection<EventScheduleDocument>(SchedulesCollectionName);
    private IMongoCollection<ReminderRecipientDocument> Recipients => database.GetCollection<ReminderRecipientDocument>(RecipientsCollectionName);

    public async Task ApplyPublishedAsync(
        Guid catalogEventId,
        string title,
        string artist,
        string venueName,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<EventScheduleDocument>.Update
            .Set(d => d.Title, title)
            .Set(d => d.Artist, artist)
            .Set(d => d.VenueName, venueName)
            .Set(d => d.StartsAt, startsAt.UtcDateTime)
            .Set(d => d.IsCancelled, false)
            .Set(d => d.Revision, revision);

        if (await ApplyGuardedAsync(catalogEventId, update, revision, cancellationToken))
            return;

        // A newer Rescheduled can legitimately create a schedule-only shell before the earlier
        // Published message arrives. Its revision must continue to win for StartsAt, while the
        // immutable metadata from Published fills the otherwise permanent empty fields.
        var placeholderFilter = Builders<EventScheduleDocument>.Filter.And(
            Builders<EventScheduleDocument>.Filter.Eq(d => d.Id, catalogEventId),
            Builders<EventScheduleDocument>.Filter.Ne(d => d.IsCancelled, true),
            Builders<EventScheduleDocument>.Filter.Or(
                Builders<EventScheduleDocument>.Filter.Eq(d => d.Title, ""),
                Builders<EventScheduleDocument>.Filter.Exists(d => d.Title, false),
                Builders<EventScheduleDocument>.Filter.Eq(d => d.Artist, ""),
                Builders<EventScheduleDocument>.Filter.Exists(d => d.Artist, false),
                Builders<EventScheduleDocument>.Filter.Eq(d => d.VenueName, ""),
                Builders<EventScheduleDocument>.Filter.Exists(d => d.VenueName, false)));
        var metadata = Builders<EventScheduleDocument>.Update
            .Set(d => d.Title, title)
            .Set(d => d.Artist, artist)
            .Set(d => d.VenueName, venueName);
        await Schedules.UpdateOneAsync(placeholderFilter, metadata, cancellationToken: cancellationToken);
    }

    public async Task ApplyRescheduledAsync(
        Guid catalogEventId,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default)
    {
        // Rescheduled carries nothing but StartsAt. If it overtakes Published, this update also
        // creates the document (see ApplyGuardedAsync) with empty placeholder fields. Published
        // may carry an older revision, so its dedicated metadata-fill path completes this shell
        // without overwriting the newer StartsAt.
        var update = Builders<EventScheduleDocument>.Update
            .Set(d => d.StartsAt, startsAt.UtcDateTime)
            .Set(d => d.Revision, revision)
            .SetOnInsert(d => d.Title, "")
            .SetOnInsert(d => d.Artist, "")
            .SetOnInsert(d => d.VenueName, "")
            .SetOnInsert(d => d.IsCancelled, false);

        await ApplyGuardedAsync(catalogEventId, update, revision, cancellationToken);
    }

    public async Task ApplyCancelledAsync(Guid catalogEventId, long revision, CancellationToken cancellationToken = default)
    {
        // No revision guard — see IEventReminderReadModel's doc comment: a Cancel is a
        // domain-enforced one-way door, so it is always safe to apply and it must always win.
        var filter = Builders<EventScheduleDocument>.Filter.Eq(d => d.Id, catalogEventId);
        var update = Builders<EventScheduleDocument>.Update
            .Set(d => d.IsCancelled, true)
            .SetOnInsert(d => d.Revision, revision)
            .SetOnInsert(d => d.Title, "")
            .SetOnInsert(d => d.Artist, "")
            .SetOnInsert(d => d.VenueName, "")
            .SetOnInsert(d => d.StartsAt, default(DateTime));

        await Schedules.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);

        // Cancellation is a one-way tombstone, but an old/v1 cancellation still must not move the
        // diagnostic revision backwards. Advance only when the incoming value is newer.
        var revisionFilter = Builders<EventScheduleDocument>.Filter.And(
            filter,
            Builders<EventScheduleDocument>.Filter.Or(
                Builders<EventScheduleDocument>.Filter.Lt(d => d.Revision, revision),
                Builders<EventScheduleDocument>.Filter.Exists(d => d.Revision, false)));
        await Schedules.UpdateOneAsync(
            revisionFilter,
            Builders<EventScheduleDocument>.Update.Set(d => d.Revision, revision),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The revision guard, shared by Published and Rescheduled. The filter's non-equality clauses
    /// (<c>IsCancelled != true</c>, <c>Revision &lt;= incoming</c>) mean an existing document that
    /// fails them will not match — and MongoDB's upsert, on no match, still tries to insert a new
    /// document using the id from the filter, which collides with the document that already owns
    /// that id and raises E11000. That is not a bug to route around; it IS the compare-and-swap:
    /// the duplicate-key error is Mongo's way of saying "the current stored state already rejects
    /// this write," which is exactly the outcome wanted. Catching it here is the documented
    /// pattern for conditional upserts in MongoDB — there is no separate "no-op" return value the
    /// driver could give instead.
    /// </summary>
    private async Task<bool> ApplyGuardedAsync(
        Guid catalogEventId,
        UpdateDefinition<EventScheduleDocument> update,
        long revision,
        CancellationToken cancellationToken)
    {
        var filter = Builders<EventScheduleDocument>.Filter.And(
            Builders<EventScheduleDocument>.Filter.Eq(d => d.Id, catalogEventId),
            Builders<EventScheduleDocument>.Filter.Ne(d => d.IsCancelled, true),
            Builders<EventScheduleDocument>.Filter.Or(
                Builders<EventScheduleDocument>.Filter.Lte(d => d.Revision, revision),
                // Documents written before Commit 11 have no Revision field, and MongoDB's $lte
                // is type-bracketed: a missing field never matches a numeric comparison. Without
                // this clause every pre-existing event fails the filter, the upsert collides on
                // _id, and the catch below swallows the E11000 as "this message loses" — silently
                // freezing every already-published event's schedule forever. Missing reads as
                // unknown, which mục 10's expand-only rule defines as 0.
                Builders<EventScheduleDocument>.Filter.Exists(d => d.Revision, false)));

        try
        {
            await Schedules.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The stored document is tombstoned or already at a higher revision — this message
            // loses, which is the correct outcome, not a failure to surface.
            return false;
        }
    }

    public async Task AddRecipientAsync(
        Guid orderId,
        Guid catalogEventId,
        Guid userId,
        string customerName,
        string customerEmail,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ReminderRecipientDocument>.Filter.Eq(d => d.Id, orderId);

        // SetOnInsert for RemindedAt, never Set: a redelivered NotifyOrderCompleted (MassTransit's
        // own retry policy can do this on transient failure, same as any other consumer) must not
        // wipe an already-set RemindedAt back to null — that would cause a genuine double-remind
        // on the next tick, the exact bug the idempotency requirement exists to prevent.
        var update = Builders<ReminderRecipientDocument>.Update
            .Set(d => d.CatalogEventId, catalogEventId)
            .Set(d => d.UserId, userId)
            .Set(d => d.CustomerName, customerName)
            .Set(d => d.CustomerEmail, customerEmail)
            .SetOnInsert(d => d.RemindedAt, null);

        await Recipients.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<IReadOnlyList<UpcomingReminder>> GetPendingRemindersAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default)
    {
        var scheduleFilter = Builders<EventScheduleDocument>.Filter.And(
            Builders<EventScheduleDocument>.Filter.Gte(d => d.StartsAt, windowStart.UtcDateTime),
            Builders<EventScheduleDocument>.Filter.Lte(d => d.StartsAt, windowEnd.UtcDateTime),
            Builders<EventScheduleDocument>.Filter.Eq(d => d.IsCancelled, false));

        var dueEvents = await Schedules.Find(scheduleFilter).ToListAsync(cancellationToken);
        if (dueEvents.Count == 0)
        {
            return [];
        }

        var dueEventIds = dueEvents.Select(e => e.Id).ToList();
        var recipientFilter = Builders<ReminderRecipientDocument>.Filter.And(
            Builders<ReminderRecipientDocument>.Filter.In(d => d.CatalogEventId, dueEventIds),
            Builders<ReminderRecipientDocument>.Filter.Eq(d => d.RemindedAt, null));

        var dueRecipients = await Recipients.Find(recipientFilter).ToListAsync(cancellationToken);
        if (dueRecipients.Count == 0)
        {
            return [];
        }

        var eventsById = dueEvents.ToDictionary(e => e.Id);
        return dueRecipients
            .Select(r =>
            {
                var eventDoc = eventsById[r.CatalogEventId];
                return new UpcomingReminder(
                    r.Id,
                    r.CatalogEventId,
                    r.UserId,
                    r.CustomerName,
                    r.CustomerEmail,
                    eventDoc.Title,
                    eventDoc.Artist,
                    eventDoc.VenueName,
                    new DateTimeOffset(eventDoc.StartsAt, TimeSpan.Zero));
            })
            .ToList();
    }

    public async Task<bool> TryMarkRemindedAsync(Guid orderId, DateTimeOffset remindedAt, CancellationToken cancellationToken = default)
    {
        // Atomic conditional update — the RemindedAt == null filter clause is what makes this
        // idempotent under concurrent/repeated ticks: a second tick racing (or replaying) the same
        // order finds ModifiedCount == 0 and the caller knows not to re-log the reminder.
        var filter = Builders<ReminderRecipientDocument>.Filter.And(
            Builders<ReminderRecipientDocument>.Filter.Eq(d => d.Id, orderId),
            Builders<ReminderRecipientDocument>.Filter.Eq(d => d.RemindedAt, null));
        var update = Builders<ReminderRecipientDocument>.Update.Set(d => d.RemindedAt, remindedAt.UtcDateTime);

        var result = await Recipients.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        return result.ModifiedCount > 0;
    }

    [BsonIgnoreExtraElements]
    private sealed class EventScheduleDocument
    {
        [BsonId]
        public Guid Id { get; set; }

        public string Title { get; set; } = null!;
        public string Artist { get; set; } = null!;
        public string VenueName { get; set; } = null!;
        public DateTime StartsAt { get; set; }
        public bool IsCancelled { get; set; }

        /// <summary>Catalog's Event.AggregateVersion at write time (Commit 11) — see IEventReminderReadModel's doc comment.</summary>
        public long Revision { get; set; }
    }

    [BsonIgnoreExtraElements]
    private sealed class ReminderRecipientDocument
    {
        [BsonId]
        public Guid Id { get; set; }

        public Guid CatalogEventId { get; set; }
        public Guid UserId { get; set; }
        public string CustomerName { get; set; } = null!;
        public string CustomerEmail { get; set; } = null!;
        public DateTime? RemindedAt { get; set; }
    }
}
