"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { fetchJson } from "@ticketflow/http-client";
import {
  isNewerSeatVersion,
  parseEventAvailabilityResponse,
  seatVersionOf,
  type SeatAvailabilityResponse,
  type SeatVersion,
} from "@ticketflow/types";

interface SeatsUpdatedPayload {
  seatIds: string[];
  status: string;
  generation: number;
  occurredAt: string;
}

interface LiveSeatState extends SeatVersion {
  status: string;
  observed: number;
}

export type LiveConnectionState = "connecting" | "live" | "polling" | "unavailable";

export function useSeatMapLive(
  eventId: string,
  signedIn: boolean,
  initialSeats: SeatAvailabilityResponse[],
  publicGatewayUrl: string,
): { revisions: Record<string, string>; statuses: Record<string, string>; state: LiveConnectionState } {
  const observation = useRef(0);
  const [liveSeats, setLiveSeats] = useState<Record<string, LiveSeatState>>(() => snapshotState(initialSeats, 0));
  const [state, setState] = useState<LiveConnectionState>(signedIn ? "connecting" : "unavailable");

  useEffect(() => {
    if (!signedIn) return;

    let connection: signalR.HubConnection | undefined;
    let cancelled = false;
    let pollingTimer: ReturnType<typeof setTimeout> | undefined;
    let pollingActive = false;
    let snapshotController: AbortController | undefined;
    let bufferingLiveUpdates = false;
    let bufferedLiveUpdates: SeatsUpdatedPayload[] = [];

    const replaceSnapshot = (seats: SeatAvailabilityResponse[]) => {
      setLiveSeats((previous) => Object.fromEntries(seats.map((seat) => {
        const version = seatVersionOf(seat);
        const current = previous[seat.seatId];
        const unchanged = current?.status === seat.status &&
          current.generation === version.generation && current.updatedAt === version.updatedAt;
        return [seat.seatId, {
          status: seat.status,
          ...version,
          observed: unchanged ? current.observed : ++observation.current,
        }];
      })));
    };

    const applyLiveUpdate = (payload: SeatsUpdatedPayload) => {
      setLiveSeats((previous) => {
        const next = { ...previous };
        const version = { generation: payload.generation, updatedAt: payload.occurredAt };
        const observed = ++observation.current;
        for (const seatId of payload.seatIds) {
          if (isNewerSeatVersion(version, previous[seatId])) {
            next[seatId] = { status: payload.status, ...version, observed };
          }
        }
        return next;
      });
    };

    const fetchSnapshot = async (): Promise<SeatAvailabilityResponse[]> => {
      snapshotController?.abort();
      snapshotController = new AbortController();
      const snapshot = await fetchJson(`/api/events/${encodeURIComponent(eventId)}/availability`, {
        cache: "no-store",
        signal: snapshotController.signal,
        timeoutMs: 5_000,
        validate: parseEventAvailabilityResponse,
      });
      return snapshot.seats;
    };

    const resyncSnapshot = async () => {
      bufferingLiveUpdates = true;
      try {
        const seats = await fetchSnapshot();
        if (!cancelled) replaceSnapshot(seats);
      } finally {
        bufferingLiveUpdates = false;
        const pending = bufferedLiveUpdates;
        bufferedLiveUpdates = [];
        if (!cancelled) pending.forEach(applyLiveUpdate);
      }
    };

    const stopPolling = () => {
      pollingActive = false;
      if (pollingTimer) clearTimeout(pollingTimer);
      pollingTimer = undefined;
    };

    const schedulePolling = () => {
      if (cancelled || pollingActive) return;
      pollingActive = true;
      setState("polling");
      const poll = async () => {
        if (cancelled || !pollingActive) return;
        pollingTimer = undefined;
        try {
          const seats = await fetchSnapshot();
          if (!cancelled && pollingActive) {
            replaceSnapshot(seats);
            setState("polling");
          }
        } catch {
          if (!cancelled && pollingActive) setState("unavailable");
        } finally {
          if (!cancelled && pollingActive) pollingTimer = setTimeout(poll, 5_000);
        }
      };
      pollingTimer = setTimeout(poll, 0);
    };

    const connect = async () => {
      const hubUrl = `${publicGatewayUrl}/reservation/hubs/seatmap`;
      const candidate = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect([0, 1_000, 3_000, 5_000])
        .build();

      candidate.on("seatsUpdated", (payload: SeatsUpdatedPayload) => {
        if (!payload || !Array.isArray(payload.seatIds) || typeof payload.status !== "string" ||
            !Number.isSafeInteger(payload.generation) || payload.generation < 0 ||
            typeof payload.occurredAt !== "string" || !Number.isFinite(Date.parse(payload.occurredAt))) return;

        if (bufferingLiveUpdates) bufferedLiveUpdates.push(payload);
        else applyLiveUpdate(payload);
      });

      candidate.onreconnecting(() => setState("connecting"));
      candidate.onreconnected(async () => {
        stopPolling();
        try {
          await candidate.invoke("JoinEvent", eventId);
          await resyncSnapshot();
          if (!cancelled) setState("live");
        } catch {
          schedulePolling();
        }
      });
      candidate.onclose(() => schedulePolling());

      const retryDelays = [0, 500, 1_500];
      for (const delay of retryDelays) {
        if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
        if (cancelled) return;
        try {
          await candidate.start();
          if (cancelled) {
            await candidate.stop();
            return;
          }
          connection = candidate;
          stopPolling();
          await candidate.invoke("JoinEvent", eventId);
          await resyncSnapshot();
          setState("live");
          return;
        } catch {
          // Initial SignalR failures are bounded; after the final attempt polling takes over.
        }
      }

      await candidate.stop().catch(() => undefined);
      schedulePolling();
    };

    void connect();

    return () => {
      cancelled = true;
      stopPolling();
      snapshotController?.abort();
      void connection?.stop();
    };
  }, [eventId, publicGatewayUrl, signedIn]);

  const { revisions, statuses } = useMemo(() => {
    const nextRevisions: Record<string, string> = {};
    const nextStatuses: Record<string, string> = {};
    for (const [seatId, seat] of Object.entries(liveSeats)) {
      nextRevisions[seatId] = `${seat.generation}:${seat.updatedAt}:${seat.observed}`;
      nextStatuses[seatId] = seat.status;
    }
    return { revisions: nextRevisions, statuses: nextStatuses };
  }, [liveSeats]);
  return { revisions, statuses, state };
}

function snapshotState(seats: SeatAvailabilityResponse[], observed: number): Record<string, LiveSeatState> {
  return Object.fromEntries(
    seats.map((seat) => [seat.seatId, { status: seat.status, ...seatVersionOf(seat), observed }]),
  );
}
