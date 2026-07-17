"use server";

import { redirect } from "next/navigation";
import type { EventResponse, SeatMapResponse, VenueResponse } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

async function post<T>(path: string, accessToken: string, body: unknown): Promise<T> {
  const res = await fetch(`${GATEWAY_URL}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`Gateway POST ${path} responded ${res.status}`);
  return res.json() as Promise<T>;
}

// Orchestrates the exact same chain organizer-facing curl calls followed to verify the purchase
// flow in Phase 7 Commit 4 — venue -> event -> seatmap -> sections -> price tiers -> reservation
// inventory -> publish — just driven by one form submit instead of manual steps
// (docs/phase-8-plan.md Commit 7). Calls are sequential on purpose: seatmap section adds mutate
// the same aggregate and each response carries the full section list, so concurrent adds from
// this one request could race and lose an update.
export async function createEventAction(formData: FormData) {
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");
  if (!session.roles?.includes("organizer")) {
    throw new Error("Only organizers can create events.");
  }
  const accessToken = session.accessToken;

  const venueMode = formData.get("venueMode") as string;
  let venueId = formData.get("venueId") as string | null;

  if (venueMode === "new" || !venueId) {
    const venue = await post<VenueResponse>("/catalog/venues", accessToken, {
      name: formData.get("venueName"),
      street: formData.get("venueStreet"),
      city: formData.get("venueCity"),
      country: formData.get("venueCountry"),
      postalCode: formData.get("venuePostalCode") || null,
    });
    venueId = venue.id;
  }

  const event = await post<EventResponse>("/catalog/events", accessToken, {
    title: formData.get("title"),
    description: formData.get("description"),
    artist: formData.get("artist"),
    venueId,
    startsAt: new Date(formData.get("startsAt") as string).toISOString(),
    endsAt: new Date(formData.get("endsAt") as string).toISOString(),
  });

  await post<SeatMapResponse>(`/catalog/events/${event.id}/seatmap`, accessToken, {});

  const sectionNames = formData.getAll("sectionName") as string[];
  const sectionCapacities = formData.getAll("sectionCapacity") as string[];
  const sectionPrices = formData.getAll("sectionPrice") as string[];

  const provisioned: { sectionId: string; sectionName: string; capacity: number }[] = [];

  for (let i = 0; i < sectionNames.length; i++) {
    const capacity = Number(sectionCapacities[i]);
    const seatMap = await post<SeatMapResponse>(
      `/catalog/events/${event.id}/seatmap/sections`,
      accessToken,
      { name: sectionNames[i], capacity },
    );
    // AddSectionAsync returns the whole section list with the new one appended last.
    const section = seatMap.sections[seatMap.sections.length - 1];

    await post(`/catalog/events/${event.id}/price-tiers`, accessToken, {
      sectionId: section.id,
      amount: Number(sectionPrices[i]),
      currency: "VND",
    });

    provisioned.push({ sectionId: section.id, sectionName: section.name, capacity });
  }

  await post(`/reservation/events/${event.id}/inventory`, accessToken, {
    sections: provisioned,
  });

  await post<EventResponse>(`/catalog/events/${event.id}/publish`, accessToken, {});

  redirect("/organizer");
}
