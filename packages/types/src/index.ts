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
