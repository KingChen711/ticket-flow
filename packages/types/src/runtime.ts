import type {
  EventAvailabilityResponse,
  EventDetailResponse,
  EventOnboardingWorkflowResponse,
  EventResponse,
  HoldQuoteResponse,
  HoldResult,
  MyOrderView,
  OrderPlacedResponse,
  RevenueResult,
  SearchDocument,
  SearchEventsResult,
  SeatAvailabilityResponse,
  TicketResponse,
  ValidateTicketResponse,
  VenueResponse,
} from "./contracts";

type ObjectValue = Record<string, unknown>;

export function parseSearchEventsResult(value: unknown): SearchEventsResult {
  const object = record(value, "search result");
  return {
    items: array(object.items, "search items").map(parseSearchDocument),
    total: nonNegativeNumber(object.total, "search total"),
  };
}

export function parseEventDetailResponse(value: unknown): EventDetailResponse {
  const object = record(value, "event detail");
  return {
    event: parseEventResponse(object.event),
    venue: parseVenueResponse(object.venue),
    seats: array(object.seats, "event seats").map(parseSeatAvailability),
    recommendations: array(object.recommendations, "recommendations").map((item) => {
      const recommendation = record(item, "recommendation");
      return {
        eventId: text(recommendation.eventId, "recommendation eventId"),
        title: text(recommendation.title, "recommendation title"),
        artist: text(recommendation.artist, "recommendation artist"),
        venueName: text(recommendation.venueName, "recommendation venueName"),
        city: text(recommendation.city, "recommendation city"),
        startsAt: isoDate(recommendation.startsAt, "recommendation startsAt"),
      };
    }),
    availabilityStatus: oneOf(
      object.availabilityStatus,
      ["Available", "TemporarilyUnavailable"] as const,
      "availability status",
    ),
  };
}

export function parseEventAvailabilityResponse(value: unknown): EventAvailabilityResponse {
  const object = record(value, "event availability");
  return {
    eventId: text(object.eventId, "availability eventId"),
    seats: array(object.seats, "availability seats").map(parseSeatAvailability),
  };
}

export function parseHoldResult(value: unknown): HoldResult {
  const object = record(value, "hold result");
  return {
    success: boolean(object.success, "hold success"),
    holdId: nullableText(object.holdId, "hold id"),
    expiresAt: nullableIsoDate(object.expiresAt, "hold expiresAt"),
    unavailableSeatIds: array(object.unavailableSeatIds, "unavailable seats").map((item) => text(item, "seat id")),
    quote: object.quote === null ? null : parseHoldQuote(object.quote),
    reason: oneOf(
      object.reason,
      ["None", "SeatsUnavailable", "HoldCapExceeded", "RateLimited"] as const,
      "hold reason",
    ),
    reasonDetail: nullableText(object.reasonDetail, "hold reasonDetail"),
  };
}

export function parseHoldQuote(value: unknown): HoldQuoteResponse {
  const object = record(value, "hold quote");
  return {
    holdId: text(object.holdId, "quote holdId"),
    eventId: text(object.eventId, "quote eventId"),
    ownerUserId: text(object.ownerUserId, "quote ownerUserId"),
    seatIds: array(object.seatIds, "quote seats").map((item) => text(item, "quote seat id")),
    amount: nonNegativeNumber(object.amount, "quote amount"),
    currency: text(object.currency, "quote currency"),
    priceSnapshotVersion: positiveNumber(object.priceSnapshotVersion, "quote priceSnapshotVersion"),
    generation: positiveNumber(object.generation, "quote generation"),
    expiresAt: isoDate(object.expiresAt, "quote expiresAt"),
  };
}

export function parseOrderPlacedResponse(value: unknown): OrderPlacedResponse {
  const object = record(value, "placed order");
  return {
    orderId: text(object.orderId, "orderId"),
    customerId: text(object.customerId, "customerId"),
    catalogEventId: text(object.catalogEventId, "catalogEventId"),
    seatIds: array(object.seatIds, "order seats").map((item) => text(item, "order seat id")),
    totalAmount: nonNegativeNumber(object.totalAmount, "order totalAmount"),
    currency: text(object.currency, "order currency"),
    status: text(object.status, "order status"),
  };
}

export function parseMyOrderView(value: unknown): MyOrderView {
  const object = record(value, "order view");
  return {
    id: text(object.id, "order id"),
    customerId: text(object.customerId, "order customerId"),
    catalogEventId: text(object.catalogEventId, "order catalogEventId"),
    seatIds: array(object.seatIds, "order seats").map((item) => text(item, "order seat id")),
    totalAmount: nonNegativeNumber(object.totalAmount, "order totalAmount"),
    currency: text(object.currency, "order currency"),
    status: text(object.status, "order status"),
    placedAt: isoDate(object.placedAt, "order placedAt"),
    confirmedAt: nullableIsoDate(object.confirmedAt, "order confirmedAt"),
    cancelledAt: nullableIsoDate(object.cancelledAt, "order cancelledAt"),
    ticketPdfUrls: object.ticketPdfUrls === null || object.ticketPdfUrls === undefined
      ? null
      : array(object.ticketPdfUrls, "ticket PDF URLs").map((item) => text(item, "ticket PDF URL")),
    sequence: object.sequence === undefined ? 0 : nonNegativeNumber(object.sequence, "order sequence"),
  };
}

export function parseMyOrderViews(value: unknown): MyOrderView[] {
  return array(value, "orders").map(parseMyOrderView);
}

export function parseTicketResponses(value: unknown): TicketResponse[] {
  return array(value, "tickets").map((item) => {
    const object = record(item, "ticket");
    return {
      id: text(object.id, "ticket id"),
      orderId: text(object.orderId, "ticket orderId"),
      seatId: text(object.seatId, "ticket seatId"),
      issuedAt: isoDate(object.issuedAt, "ticket issuedAt"),
      checkedInAt: nullableIsoDate(object.checkedInAt, "ticket checkedInAt"),
    };
  });
}

export function parseEventResponses(value: unknown): EventResponse[] {
  return array(value, "events").map(parseEventResponse);
}

export function parseVenueResponses(value: unknown): VenueResponse[] {
  return array(value, "venues").map(parseVenueResponse);
}

export function parseWorkflowResponse(value: unknown): EventOnboardingWorkflowResponse {
  const object = record(value, "onboarding workflow");
  return {
    workflowId: text(object.workflowId, "workflow id"),
    creationKey: text(object.creationKey, "workflow creationKey"),
    venueId: text(object.venueId, "workflow venueId"),
    eventId: text(object.eventId, "workflow eventId"),
    status: oneOf(
      object.status,
      ["Running", "Failed", "Completed", "Compensating", "CompensationFailed", "Compensated"] as const,
      "workflow status",
    ),
    lastCompletedStep: text(object.lastCompletedStep, "workflow lastCompletedStep"),
    failedStep: nullableText(object.failedStep, "workflow failedStep"),
    lastError: nullableText(object.lastError, "workflow lastError"),
    canResume: boolean(object.canResume, "workflow canResume"),
    canCompensate: boolean(object.canCompensate, "workflow canCompensate"),
    updatedAt: isoDate(object.updatedAt, "workflow updatedAt"),
  };
}

export function parseRevenueResult(value: unknown): RevenueResult {
  const object = record(value, "revenue result");
  return {
    totalRevenue: nullableNonNegativeNumber(object.totalRevenue, "total revenue"),
    totalSeats: nonNegativeNumber(object.totalSeats, "total seats"),
    byEvent: array(object.byEvent, "event revenue").map((item) => {
      const revenue = record(item, "event revenue");
      return {
        eventId: text(revenue.eventId, "revenue eventId"),
        currency: text(revenue.currency, "event revenue currency"),
        revenue: nonNegativeNumber(revenue.revenue, "event revenue"),
        seatCount: nonNegativeNumber(revenue.seatCount, "event seatCount"),
      };
    }),
    byCurrency: array(object.byCurrency, "currency revenue").map((item) => {
      const revenue = record(item, "currency revenue");
      return {
        currency: text(revenue.currency, "currency revenue currency"),
        revenue: nonNegativeNumber(revenue.revenue, "currency revenue"),
        seatCount: nonNegativeNumber(revenue.seatCount, "currency revenue seatCount"),
      };
    }),
  };
}

export function parseValidateTicketResponse(value: unknown): ValidateTicketResponse {
  const object = record(value, "ticket validation");
  return {
    status: oneOf(
      object.status,
      ["Valid", "AlreadyCheckedIn", "NotFound", "WrongEvent", "Revoked"] as const,
      "ticket status",
    ),
    ticketId: nullableText(object.ticketId, "ticket id"),
    checkedInAt: nullableIsoDate(object.checkedInAt, "ticket checkedInAt"),
  };
}

export function parseUnknown(value: unknown): unknown {
  return value;
}

function parseSearchDocument(value: unknown): SearchDocument {
  const object = record(value, "search event");
  return {
    eventId: text(object.eventId, "search eventId"),
    title: text(object.title, "search title"),
    artist: text(object.artist, "search artist"),
    venueName: text(object.venueName, "search venueName"),
    city: text(object.city, "search city"),
    startsAt: isoDate(object.startsAt, "search startsAt"),
    status: text(object.status, "search status"),
    timeZoneId: object.timeZoneId === undefined ? "UTC" : timeZone(object.timeZoneId, "search timeZoneId"),
  };
}

function parseEventResponse(value: unknown): EventResponse {
  const object = record(value, "event");
  return {
    id: text(object.id, "event id"),
    title: text(object.title, "event title"),
    description: text(object.description, "event description", true),
    artist: text(object.artist, "event artist"),
    venueId: text(object.venueId, "event venueId"),
    startsAt: isoDate(object.startsAt, "event startsAt"),
    endsAt: isoDate(object.endsAt, "event endsAt"),
    status: text(object.status, "event status"),
    priceTiers: array(object.priceTiers, "price tiers").map((item) => {
      const tier = record(item, "price tier");
      return {
        id: text(tier.id, "price tier id"),
        sectionId: text(tier.sectionId, "price tier sectionId"),
        amount: nonNegativeNumber(tier.amount, "price tier amount"),
        currency: text(tier.currency, "price tier currency"),
      };
    }),
    timeZoneId: object.timeZoneId === undefined ? "UTC" : timeZone(object.timeZoneId, "event timeZoneId"),
  };
}

function parseVenueResponse(value: unknown): VenueResponse {
  const object = record(value, "venue");
  return {
    id: text(object.id, "venue id"),
    name: text(object.name, "venue name"),
    street: text(object.street, "venue street"),
    city: text(object.city, "venue city"),
    country: text(object.country, "venue country"),
    postalCode: nullableText(object.postalCode, "venue postalCode"),
  };
}

function parseSeatAvailability(value: unknown): SeatAvailabilityResponse {
  const object = record(value, "seat availability");
  return {
    seatId: text(object.seatId ?? object.id, "seat id"),
    sectionId: text(object.sectionId, "seat sectionId"),
    label: text(object.label, "seat label"),
    status: text(object.status, "seat status"),
    generation: object.generation === undefined ? 0 : nonNegativeNumber(object.generation, "seat generation"),
    updatedAt: object.updatedAt === undefined ? null : nullableIsoDate(object.updatedAt, "seat updatedAt"),
  };
}

function record(value: unknown, field: string): ObjectValue {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError(`${field} must be an object.`);
  }
  return value as ObjectValue;
}

function array(value: unknown, field: string): unknown[] {
  if (!Array.isArray(value)) throw new TypeError(`${field} must be an array.`);
  return value;
}

function text(value: unknown, field: string, allowEmpty = false): string {
  if (typeof value !== "string" || (!allowEmpty && value.trim().length === 0)) {
    throw new TypeError(`${field} must be a${allowEmpty ? "" : " non-empty"} string.`);
  }
  return value;
}

function nullableText(value: unknown, field: string): string | null {
  if (value === null || value === undefined) return null;
  return text(value, field);
}

function boolean(value: unknown, field: string): boolean {
  if (typeof value !== "boolean") throw new TypeError(`${field} must be a boolean.`);
  return value;
}

function nonNegativeNumber(value: unknown, field: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    throw new TypeError(`${field} must be a non-negative number.`);
  }
  return value;
}

function nullableNonNegativeNumber(value: unknown, field: string): number | null {
  if (value === null) return null;
  return nonNegativeNumber(value, field);
}

function positiveNumber(value: unknown, field: string): number {
  const result = nonNegativeNumber(value, field);
  if (result <= 0) throw new TypeError(`${field} must be positive.`);
  return result;
}

function isoDate(value: unknown, field: string): string {
  const result = text(value, field);
  if (!Number.isFinite(Date.parse(result))) throw new TypeError(`${field} must be an ISO date.`);
  return result;
}

function nullableIsoDate(value: unknown, field: string): string | null {
  if (value === null || value === undefined) return null;
  return isoDate(value, field);
}

function timeZone(value: unknown, field: string): string {
  const result = text(value, field);
  try {
    new Intl.DateTimeFormat("en", { timeZone: result }).format(0);
  } catch {
    throw new TypeError(`${field} must be a valid IANA time zone.`);
  }
  return result;
}

function oneOf<const T extends readonly string[]>(value: unknown, options: T, field: string): T[number] {
  if (typeof value !== "string" || !options.includes(value)) {
    throw new TypeError(`${field} must be one of ${options.join(", ")}.`);
  }
  return value as T[number];
}
