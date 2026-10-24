import type { MyOrderView, SeatAvailabilityResponse } from "./contracts";

export interface ZonedDateTimeSubmission {
  utc: string;
  utcOffsetMinutes: number;
}

export function serializeLocalDateTime(value: string): ZonedDateTimeSubmission {
  const instant = new Date(value);
  if (!value || !Number.isFinite(instant.getTime())) throw new TypeError("Invalid local date/time.");
  return {
    utc: instant.toISOString(),
    utcOffsetMinutes: -instant.getTimezoneOffset(),
  };
}

export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
}

export function formatEventDateTime(value: string, timeZoneId: string): string {
  return new Intl.DateTimeFormat("en-GB", {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: timeZoneId,
    timeZoneName: "short",
  }).format(new Date(value));
}

export function shouldApplyOrderUpdate(current: MyOrderView, incoming: MyOrderView): boolean {
  return incoming.id === current.id && incoming.sequence > current.sequence;
}

export interface SeatVersion {
  generation: number;
  updatedAt: string | null;
}

export function seatVersionOf(seat: SeatAvailabilityResponse): SeatVersion {
  return { generation: seat.generation, updatedAt: seat.updatedAt };
}

export function isNewerSeatVersion(incoming: SeatVersion, current?: SeatVersion): boolean {
  if (!current) return true;
  if (incoming.generation !== current.generation) return incoming.generation > current.generation;
  if (!incoming.updatedAt) return false;
  if (!current.updatedAt) return true;
  return Date.parse(incoming.updatedAt) > Date.parse(current.updatedAt);
}
