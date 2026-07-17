// Backend DTO types, hand-written per-screen need (see docs/phase-8-plan.md mục 6).
// Field names match the C# records' camelCase JSON serialization exactly.

// search-service — GET /search/events (Elasticsearch-backed, supports q/artist/city/from/to)
export interface SearchDocument {
  eventId: string;
  title: string;
  artist: string;
  venueName: string;
  city: string;
  startsAt: string;
  status: string;
}

export interface SearchEventsResult {
  items: SearchDocument[];
  total: number;
}

// catalog-service — GET /catalog/events/{id}/detail (Phase 6 aggregator)
export interface PriceTierResponse {
  id: string;
  sectionId: string;
  amount: number;
  currency: string;
}

export interface EventResponse {
  id: string;
  title: string;
  description: string;
  artist: string;
  venueId: string;
  startsAt: string;
  endsAt: string;
  status: string;
  priceTiers: PriceTierResponse[];
}

export interface VenueResponse {
  id: string;
  name: string;
  street: string;
  city: string;
  country: string;
  postalCode: string | null;
}

export interface SeatAvailabilityResponse {
  seatId: string;
  sectionId: string;
  label: string;
  status: string;
}

export interface RecommendedEventSummary {
  eventId: string;
  title: string;
  artist: string;
  venueName: string;
  city: string;
  startsAt: string;
}

export interface EventDetailResponse {
  event: EventResponse;
  venue: VenueResponse;
  seats: SeatAvailabilityResponse[];
  recommendations: RecommendedEventSummary[];
}

// reservation-service — POST /reservation/holds
export interface HoldResult {
  success: boolean;
  holdId: string;
  expiresAt: string;
  unavailableSeatIds: string[];
}

// ordering-service — POST /ordering/orders response (note: "orderId", not "id" — the GET
// endpoints below return MyOrderView, which uses "id" instead; two different response shapes
// for the same underlying order, matching the write/read split of Ch6 CQRS).
export interface OrderPlacedResponse {
  orderId: string;
  customerId: string;
  catalogEventId: string;
  seatIds: string[];
  totalAmount: number;
  currency: string;
  status: string;
}

// ordering-service — GET /ordering/orders and GET /ordering/orders/{id} (MyOrderView projection)
export interface MyOrderView {
  id: string;
  customerId: string;
  catalogEventId: string;
  seatIds: string[];
  totalAmount: number;
  currency: string;
  status: string;
  placedAt: string;
  confirmedAt: string | null;
  cancelledAt: string | null;
}

// ticketing-service — GET /ticketing/orders/{orderId}/tickets
export interface TicketResponse {
  id: string;
  orderId: string;
  seatId: string;
  issuedAt: string;
  checkedInAt: string | null;
}

// catalog-service — POST/GET /catalog/events/{eventId}/seatmap, POST .../seatmap/sections
export interface SectionResponse {
  id: string;
  name: string;
  capacity: number;
}

export interface SeatMapResponse {
  id: string;
  eventId: string;
  sections: SectionResponse[];
}

// analytics-service — GET /analytics/revenue (organizer-dashboard)
export interface EventRevenue {
  eventId: string;
  revenue: number;
  seatCount: number;
}

export interface RevenueResult {
  totalRevenue: number;
  totalSeats: number;
  byEvent: EventRevenue[];
}
