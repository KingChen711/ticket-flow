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
        <p className="text-gray-500">Sign in with a gate-staff or organizer account to scan tickets.</p>
      </div>
    );
  }

  if (!session.roles?.some((role) => ["gate-staff", "organizer", "admin"].includes(role))) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="text-red-600">
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
    <form action="/scanner" className="mx-auto flex max-w-md flex-col gap-4 p-6">
      <h1 className="text-xl font-semibold">Choose gate context</h1>
      <p className="text-sm text-gray-600">
        Each scan is checked online against the selected event and your gate assignment.
      </p>
      <label className="flex flex-col gap-1 text-sm">
        Event ID
        <input
          name="eventId"
          required
          pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}"
          className="rounded border px-3 py-2"
        />
      </label>
      <label className="flex flex-col gap-1 text-sm">
        Gate label
        <input name="gate" required maxLength={80} className="rounded border px-3 py-2" />
      </label>
      <button type="submit" className="rounded bg-black px-4 py-2 text-white">
        Start scanner
      </button>
      <p className="text-xs text-gray-500">
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
