import type { EventOnboardingWorkflowResponse, VenueResponse } from "@ticketflow/types";
import { auth } from "@/auth";
import { fetchGateway, fetchGatewayAuthed } from "@/lib/gateway";
import { NewEventForm } from "./NewEventForm";
import { randomUUID } from "node:crypto";
import { redirect } from "next/navigation";

// Same role gate as the dashboard (docs/phase-8-plan.md Commit 7) — the layout only hides the
// nav link for non-organizers, it doesn't stop someone from navigating here directly.
export default async function NewEventPage({
  searchParams,
}: {
  searchParams: Promise<{ creationKey?: string | string[] }>;
}) {
  const session = await auth();

  if (!session) {
    return (
      <div className="mx-auto max-w-2xl p-6">
        <p className="text-gray-500">Sign in with an organizer account to create events.</p>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="mx-auto max-w-2xl p-6">
        <p className="text-red-600">
          This app is for organizers only. Your account does not have the organizer role.
        </p>
      </div>
    );
  }

  // GET /catalog/venues is public (anonymous route) — fetched without a token, same as
  // customer-web's browse page.
  const venues = await fetchGateway<VenueResponse[]>("/catalog/venues");
  const query = await searchParams;
  const suppliedKey = typeof query.creationKey === "string" ? query.creationKey.trim() : "";
  const creationKey = suppliedKey.length > 0 && suppliedKey.length <= 128 ? suppliedKey : randomUUID();
  let initialWorkflow: EventOnboardingWorkflowResponse | null = null;

  if (suppliedKey && session.accessToken) {
    try {
      initialWorkflow = await fetchGatewayAuthed<EventOnboardingWorkflowResponse>(
        `/catalog/workflows/event-onboarding/by-key/${encodeURIComponent(creationKey)}`,
        session.accessToken,
      );
    } catch {
      // No workflow for a newly minted key is the normal first-render case. The server action
      // performs the same reconciliation after an uncertain POST response.
    }
  }

  if (initialWorkflow?.status === "Completed") redirect("/organizer");

  return (
    <div className="mx-auto max-w-2xl p-6">
      <h1 className="mb-4 text-xl font-semibold">New event</h1>
      <NewEventForm venues={venues} creationKey={creationKey} initialWorkflow={initialWorkflow} />
    </div>
  );
}
