"use client";

import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { applySeatsUpdated, type SeatsUpdatedPayload } from "./seatSelection";


// Browser connects directly to the reservation hub (the one deliberate exception to the BFF
// pattern, docs/phase-8-plan.md mục 2/5) — hold/release/confirm broadcasts land here live.
// No reconnect-time re-fetch of missed updates: a hold's TTL expiry never broadcasts at all
// (Redis has no keyspace-notification wiring yet, same doc's known trade-off) and this project
// doesn't try to paper over that with a fetch-on-reconnect — a stale seat map self-heals the
// next time the viewer actually interacts with it (submits a hold, or reloads).
export function useSeatMapLive(eventId: string, signedIn: boolean): Record<string, string> {
  const [liveStatuses, setLiveStatuses] = useState<Record<string, string>>({});

  useEffect(() => {
    if (!signedIn) return;

    let connection: signalR.HubConnection | undefined;
    let cancelled = false;

    async function connect() {
      const hubUrl = `${process.env.NEXT_PUBLIC_GATEWAY_URL}/reservation/hubs/seatmap`;
      const newConnection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, {
          accessTokenFactory: async () => {
            const res = await fetch("/api/signalr-token");
            const data = await res.json();
            return data.accessToken as string;
          },
        })
        .withAutomaticReconnect()
        .build();

      newConnection.on("seatsUpdated", (payload: SeatsUpdatedPayload) => {
        setLiveStatuses((prev) => applySeatsUpdated(prev, payload));
      });

      await newConnection.start();
      if (cancelled) {
        await newConnection.stop();
        return;
      }
      await newConnection.invoke("JoinEvent", eventId);
      connection = newConnection;
    }

    connect().catch((err) => console.error("SeatMap live connection failed:", err));

    return () => {
      cancelled = true;
      connection?.stop();
    };
  }, [eventId, signedIn]);

  return liveStatuses;
}
