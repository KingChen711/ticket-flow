"use server";

import { HttpError } from "@ticketflow/http-client";
import { parseWorkflowResponse, type EventOnboardingWorkflowResponse } from "@ticketflow/types";
import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGatewayAuthed } from "@/lib/gateway";

export interface CreateEventActionState {
  workflow: EventOnboardingWorkflowResponse | null;
  error: string | null;
}

async function postWorkflow(path: string, accessToken: string, body: unknown) {
  return fetchGatewayAuthed(path, accessToken, parseWorkflowResponse, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

async function getWorkflow(path: string, accessToken: string) {
  try {
    return await fetchGatewayAuthed(path, accessToken, parseWorkflowResponse);
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) return null;
    throw error;
  }
}

export async function createEventAction(
  previousState: CreateEventActionState,
  formData: FormData,
): Promise<CreateEventActionState> {
  const session = await auth();
  if (!session?.serverAccessToken || session.error) redirect("/api/auth/signin");
  if (!session.roles?.includes("organizer")) return { workflow: null, error: "Only organizers can create events." };

  const existingWorkflowId = formData.get("workflowId")?.toString();
  const creationKey = formData.get("creationKey")?.toString();
  let workflow: EventOnboardingWorkflowResponse | null = null;
  let requestError: string | null = null;
  try {
    if (existingWorkflowId) {
      workflow = await postWorkflow(`/catalog/workflows/event-onboarding/${existingWorkflowId}/resume`, session.serverAccessToken, {});
    } else {
      const venueMode = formData.get("venueMode")?.toString();
      const existingVenueId = venueMode === "existing" ? formData.get("venueId")?.toString() : null;
      const sectionNames = formData.getAll("sectionName").map(String);
      const sectionCapacities = formData.getAll("sectionCapacity").map(String);
      const sectionPrices = formData.getAll("sectionPrice").map(String);
      const startsAt = formData.get("startsAtUtc")?.toString();
      const endsAt = formData.get("endsAtUtc")?.toString();
      const timeZoneId = formData.get("timeZoneId")?.toString();
      const startOffset = formData.get("startUtcOffsetMinutes")?.toString();
      const endOffset = formData.get("endUtcOffsetMinutes")?.toString();
      if (!startsAt || !endsAt || !timeZoneId || startOffset === undefined || startOffset === "" ||
          endOffset === undefined || endOffset === "") {
        return { workflow: previousState.workflow, error: "Browser timezone data is missing. Reload the form and try again." };
      }
      const startUtcOffsetMinutes = Number(startOffset);
      const endUtcOffsetMinutes = Number(endOffset);
      if (!Number.isInteger(startUtcOffsetMinutes) || Math.abs(startUtcOffsetMinutes) > 14 * 60 ||
          !Number.isInteger(endUtcOffsetMinutes) || Math.abs(endUtcOffsetMinutes) > 14 * 60) {
        return { workflow: previousState.workflow, error: "Browser timezone offset is invalid. Reload the form and try again." };
      }

      workflow = await postWorkflow("/catalog/workflows/event-onboarding", session.serverAccessToken, {
        creationKey,
        existingVenueId: existingVenueId || null,
        newVenue: venueMode === "new" ? {
          name: formData.get("venueName"),
          street: formData.get("venueStreet"),
          city: formData.get("venueCity"),
          country: formData.get("venueCountry"),
          postalCode: formData.get("venuePostalCode") || null,
        } : null,
        title: formData.get("title"),
        description: formData.get("description"),
        artist: formData.get("artist"),
        startsAt,
        endsAt,
        timeZoneId,
        startUtcOffsetMinutes,
        endUtcOffsetMinutes,
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
    try {
      workflow = existingWorkflowId
        ? await getWorkflow(`/catalog/workflows/event-onboarding/${existingWorkflowId}`, session.serverAccessToken)
        : creationKey
          ? await getWorkflow(`/catalog/workflows/event-onboarding/by-key/${encodeURIComponent(creationKey)}`, session.serverAccessToken)
          : null;
    } catch {
      // Keep the original mutation error; it is the useful diagnostic.
    }
    if (!workflow) return { workflow: previousState.workflow, error: requestError };
  }

  if (workflow.status === "Completed") redirect("/organizer");
  return { workflow, error: workflow.lastError ?? requestError };
}
