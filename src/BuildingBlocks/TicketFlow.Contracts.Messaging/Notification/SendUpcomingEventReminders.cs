namespace TicketFlow.Contracts.Messaging.Notification;

/// <summary>
/// Command published by the EventReminderTimer Lambda (docs/phase-9-plan.md Phase 9 Commit 4,
/// mục 5) on an EventBridge schedule. Deliberately carries nothing but the trigger timestamp —
/// the Lambda is thin by design, every "who needs reminding, within what window" decision lives
/// in SendUpcomingEventRemindersConsumer, which owns the reminder read model. TriggeredAt is the
/// EventBridge tick's own clock, used as "now" for the reminder window query instead of the
/// consumer reading its own clock — the trigger IS the time signal in a scheduled system.
/// </summary>
public sealed record SendUpcomingEventReminders(DateTimeOffset TriggeredAt);
