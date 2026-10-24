import { auth } from "@/auth";
import { ScannerClient } from "./ScannerClient";

type ScannerSearchParams = Promise<{ eventId?: string | string[]; gate?: string | string[] }>;

// The backend still makes the authoritative event + gate assignment decision. This page only
// avoids rendering a scanner for roles that can never reach that decision.
export default async function ScannerPage({ searchParams }: { searchParams: ScannerSearchParams }) {
  const session = await auth();

  if (!session || session.error || !session.serverAccessToken) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="card text-center text-muted">Sign in with a gate-staff or organizer account to scan tickets.</p>
      </div>
    );
  }

  if (!session.roles?.some((role) => ["gate-staff", "organizer", "admin"].includes(role))) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p role="alert" className="alert-danger">
          Your account does not have a gate-scanner role.
        </p>
      </div>
    );
  }

  const query = await searchParams;
  const eventId = one(query.eventId);
  const gateLabel = one(query.gate);

  // A valid event context is mandatory. The BFF passes it unchanged to Ticketing, where it is
  // checked against both the ticket and the caller's persisted gate assignment; it is never inferred from
  // the QR payload or trusted merely because it came from this form.
  if (!isUuid(eventId) || !isGateLabel(gateLabel)) {
    return <ScannerContextForm />;
  }

  return <ScannerClient eventId={eventId} gateLabel={gateLabel} />;
}

function ScannerContextForm() {
  return (
    <form action="/scanner/" className="mx-auto flex max-w-md flex-col gap-5 p-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Choose gate context</h1>
        <p className="mt-1 text-sm text-muted">
          Each scan is checked online against the selected event and your gate assignment.
        </p>
      </div>
      <div className="field">
        <label htmlFor="eventId" className="label">Event ID</label>
        <input
          id="eventId"
          name="eventId"
          required
          autoComplete="off"
          spellCheck={false}
          pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}"
          aria-describedby="eventId-hint"
          className="input font-mono text-sm"
        />
        <p id="eventId-hint" className="hint">The UUID shown on the organizer dashboard.</p>
      </div>
      <div className="field">
        <label htmlFor="gate" className="label">Gate label</label>
        <input id="gate" name="gate" required maxLength={80} autoComplete="off" className="input" />
      </div>
      <button type="submit" className="btn-primary min-h-14 text-base">
        Start scanner
      </button>
      <p className="hint">
        Gate IDs are authorization scopes. Ask the event owner to assign your account to this exact gate.
      </p>
    </form>
  );
}

function one(value: string | string[] | undefined): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function isUuid(value: string | undefined): value is string {
  return value !== undefined && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function isGateLabel(value: string | undefined): value is string {
  return value !== undefined && value.trim().length > 0 && value.length <= 80;
}
