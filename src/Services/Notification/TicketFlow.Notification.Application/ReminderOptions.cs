namespace TicketFlow.Notification.Application;

/// <summary>
/// Bound from config section "Reminder" (docs/phase-9-plan.md mục 6 — "cửa sổ + lịch đều
/// configurable"). WindowHours is the "starts within the next N hours" reminder window; the
/// EventBridge schedule that triggers the tick is a separate, infrastructure-side knob (the init
/// deploy script's schedule expression) — the two are independent so dev can tick every minute
/// against the same 24h business window prod uses, or a shorter window if a test scenario needs it.
/// </summary>
public sealed class ReminderOptions
{
    public double WindowHours { get; set; } = 24;
}
