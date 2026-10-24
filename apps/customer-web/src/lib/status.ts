// Maps backend status strings to the shared badge styles in globals.css. Every badge also renders
// the status as text, so color is never the only signal.
const ORDER_TONES: Record<string, string> = {
  Confirmed: "badge-success",
  Cancelled: "badge-danger",
  RefundFailed: "badge-danger",
  ManualReview: "badge-warning",
};

const EVENT_TONES: Record<string, string> = {
  Published: "badge-success",
  Rescheduled: "badge-warning",
  Cancelled: "badge-danger",
};

export function orderBadge(status: string): string {
  return ORDER_TONES[status] ?? "badge-info";
}

export function eventBadge(status: string): string {
  return EVENT_TONES[status] ?? "badge-neutral";
}

/** Splits camel-case status codes for display: "ManualReview" -> "Manual review". */
export function statusLabel(status: string): string {
  const spaced = status.replace(/([a-z])([A-Z])/g, "$1 $2");
  return spaced.charAt(0) + spaced.slice(1).toLowerCase();
}

export function formatMoney(amount: number, currency: string): string {
  return `${amount.toLocaleString()} ${currency}`;
}
