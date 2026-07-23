"use server";

import { redirect } from "next/navigation";
import type { EventOnboardingWorkflowResponse } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export interface CreateEventActionState {
  workflow: EventOnboardingWorkflowResponse | null;
  error: string | null;
}

async function postWorkflow(
  path: string,
  accessToken: string,
  body: unknown,
): Promise<EventOnboardingWorkflowResponse> {
  const response = await fetch(`${GATEWAY_URL}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const detail = await response.text();
    throw new Error(`Workflow responded ${response.status}${detail ? `: ${detail}` : ""}`);
  }
  return response.json() as Promise<EventOnboardingWorkflowResponse>;
}

async function getWorkflow(
  path: string,
  accessToken: string,
): Promise<EventOnboardingWorkflowResponse | null> {
  const response = await fetch(`${GATEWAY_URL}${path}`, {
    cache: "no-store",
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Workflow lookup responded ${response.status}`);
  return response.json() as Promise<EventOnboardingWorkflowResponse>;
}

// One browser intent -> one backend workflow. The client no longer owns partial resource ids or
// decides which step should run next; it only starts a CreationKey or asks the backend to resume
// the persisted workflow id returned by an earlier attempt.
export async function createEventAction(
  previousState: CreateEventActionState,
  formData: FormData,
): Promise<CreateEventActionState> {
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");
  if (!session.roles?.includes("organizer"))
    return { workflow: null, error: "Only organizers can create events." };

  const existingWorkflowId = formData.get("workflowId")?.toString();
  const creationKey = formData.get("creationKey")?.toString();
  let workflow: EventOnboardingWorkflowResponse | null = null;
  let requestError: string | null = null;
  try {
    if (existingWorkflowId) {
      workflow = await postWorkflow(
        `/catalog/workflows/event-onboarding/${existingWorkflowId}/resume`,
        session.accessToken,
        {},
      );
    } else {
      const venueMode = formData.get("venueMode")?.toString();
      const existingVenueId = venueMode === "existing" ? formData.get("venueId")?.toString() : null;
      const sectionNames = formData.getAll("sectionName").map(String);
      const sectionCapacities = formData.getAll("sectionCapacity").map(String);
      const sectionPrices = formData.getAll("sectionPrice").map(String);

      workflow = await postWorkflow("/catalog/workflows/event-onboarding", session.accessToken, {
        creationKey: formData.get("creationKey"),
        existingVenueId: existingVenueId || null,
        newVenue:
          venueMode === "new"
            ? {
                name: formData.get("venueName"),
                street: formData.get("venueStreet"),
                city: formData.get("venueCity"),
                country: formData.get("venueCountry"),
                postalCode: formData.get("venuePostalCode") || null,
              }
            : null,
        title: formData.get("title"),
        description: formData.get("description"),
        artist: formData.get("artist"),
        startsAt: new Date(formData.get("startsAt") as string).toISOString(),
        endsAt: new Date(formData.get("endsAt") as string).toISOString(),
        sections: sectionNames.map((name, index) => ({
          name,
          capacity: Number(sectionCapacities[index]),
          price: Number(sectionPrices[index]),
          currency: "VND",
        })),
      });
    }
  } catch (error) {
    requestError = error instanceof Error ? error.message : "Workflow request failed.";

    // A POST may have committed while its HTTP response was lost. Reconcile from the durable
    // workflow before asking the organizer to submit a new intent. If the lookup is also down,
    // retain the previous workflow id so the next click still resumes rather than starts over.
    try {
      workflow = existingWorkflowId
        ? await getWorkflow(
            `/catalog/workflows/event-onboarding/${existingWorkflowId}`,
            session.accessToken,
          )
        : creationKey
          ? await getWorkflow(
              `/catalog/workflows/event-onboarding/by-key/${encodeURIComponent(creationKey)}`,
              session.accessToken,
            )
          : null;
    } catch {
      // Preserve the original request error; it is the useful diagnostic for the organizer.
    }

    if (!workflow)
      return { workflow: previousState.workflow, error: requestError };
  }

  if (workflow.status === "Completed") redirect("/organizer");
  return { workflow, error: workflow.lastError ?? requestError };
}
