import { parseVenueResponses, parseWorkflowResponse, type EventOnboardingWorkflowResponse } from "@ticketflow/types";
import { HttpError } from "@ticketflow/http-client";
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

  if (!session || session.error || !session.serverAccessToken) {
    return (
      <div className="container-page max-w-xl">
        <p className="card text-center text-muted">Sign in with an organizer account to create events.</p>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="container-page max-w-xl">
        <p role="alert" className="alert-danger">
          This app is for organizers only. Your account does not have the organizer role.
        </p>
      </div>
    );
  }

  // GET /catalog/venues is public (anonymous route) — fetched without a token, same as
  // customer-web's browse page.
  const venues = await fetchGateway("/catalog/venues", parseVenueResponses);
  const query = await searchParams;
  const suppliedKey = typeof query.creationKey === "string" ? query.creationKey.trim() : "";
  const creationKey = suppliedKey.length > 0 && suppliedKey.length <= 128 ? suppliedKey : randomUUID();
  let initialWorkflow: EventOnboardingWorkflowResponse | null = null;

  if (suppliedKey && session.serverAccessToken && !session.error) {
    try {
      initialWorkflow = await fetchGatewayAuthed(
        `/catalog/workflows/event-onboarding/by-key/${encodeURIComponent(creationKey)}`,
        session.serverAccessToken,
        parseWorkflowResponse,
      );
    } catch (error) {
      if (!(error instanceof HttpError && error.status === 404)) throw error;
      // No workflow for a newly minted key is the normal first-render case. The server action
      // performs the same reconciliation after an uncertain POST response.
    }
  }

  if (initialWorkflow?.status === "Completed") redirect("/organizer");

  return (
    <div className="container-page max-w-3xl">
      <h1 className="text-2xl font-bold tracking-tight">New event</h1>
      <p className="mb-6 mt-1 text-sm text-muted">
        The event is created, priced, stocked and published in one step. If a step fails you can resume it without duplicates.
      </p>
      <NewEventForm venues={venues} creationKey={creationKey} initialWorkflow={initialWorkflow} />
    </div>
  );
}
