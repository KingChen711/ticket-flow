import { auth } from "@/auth";
import { ScannerClient } from "./ScannerClient";

type ScannerSearchParams = Promise<{ eventId?: string | string[]; gate?: string | string[] }>;

// Same role gate as organizer-dashboard (docs/phase-8-plan.md Commit 7/8) — POST
// /tickets/validate is RequireOrganizer at the backend, this page refuses to render the scanner
// for a signed-in customer instead of letting every scan attempt 403.
export default async function ScannerPage({ searchParams }: { searchParams: ScannerSearchParams }) {
  const session = await auth();

  if (!session || session.error || !session.serverAccessToken) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="text-gray-500">Sign in with an organizer account to scan tickets.</p>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="text-red-600">
          This app is for organizers only. Your account does not have the organizer role.
        </p>
      </div>
    );
  }

  const query = await searchParams;
  const eventId = one(query.eventId);
  const gateLabel = one(query.gate);

  // A valid event context is mandatory. The BFF passes it unchanged to Ticketing, where it is
  // checked against both the ticket and the organizer's assigned event; it is never inferred from
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
        Each scan is checked online against the selected event and your organizer assignment.
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
        Gate labels identify the station for staff; authorization is enforced by the event assignment.
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
