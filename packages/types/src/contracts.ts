export interface SearchDocument {
  eventId: string;
  title: string;
  artist: string;
  venueName: string;
  city: string;
  startsAt: string;
  status: string;
  timeZoneId: string;
}

export interface SearchEventsResult {
  items: SearchDocument[];
  total: number;
}

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
  timeZoneId: string;
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
  generation: number;
  updatedAt: string | null;
}

export interface EventAvailabilityResponse {
  eventId: string;
  seats: SeatAvailabilityResponse[];
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
  availabilityStatus: "Available" | "TemporarilyUnavailable";
}

export interface HoldQuoteResponse {
  holdId: string;
  eventId: string;
  ownerUserId: string;
  seatIds: string[];
  amount: number;
  currency: string;
  priceSnapshotVersion: number;
  generation: number;
  expiresAt: string;
}

export interface HoldResult {
  success: boolean;
  holdId: string | null;
  expiresAt: string | null;
  unavailableSeatIds: string[];
  quote: HoldQuoteResponse | null;
  reason: "None" | "SeatsUnavailable" | "HoldCapExceeded" | "RateLimited";
  reasonDetail: string | null;
}

export interface OrderPlacedResponse {
  orderId: string;
  customerId: string;
  catalogEventId: string;
  seatIds: string[];
  totalAmount: number;
  currency: string;
  status: string;
}

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
  ticketPdfUrls: string[] | null;
  sequence: number;
}

export interface TicketResponse {
  id: string;
  orderId: string;
  seatId: string;
  issuedAt: string;
  checkedInAt: string | null;
}

export interface ValidateTicketResponse {
  status: "Valid" | "AlreadyCheckedIn" | "NotFound" | "WrongEvent" | "Revoked";
  ticketId: string | null;
  checkedInAt: string | null;
}

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

export interface EventOnboardingWorkflowResponse {
  workflowId: string;
  creationKey: string;
  venueId: string;
  eventId: string;
  status: "Running" | "Failed" | "Completed" | "Compensating" | "CompensationFailed" | "Compensated";
  lastCompletedStep: string;
  failedStep: string | null;
  lastError: string | null;
  canResume: boolean;
  canCompensate: boolean;
  updatedAt: string;
}

export interface EventRevenue {
  eventId: string;
  currency: string;
  revenue: number;
  seatCount: number;
}

export interface CurrencyRevenue {
  currency: string;
  revenue: number;
  seatCount: number;
}

export interface RevenueResult {
  totalRevenue: number | null;
  totalSeats: number;
  byEvent: EventRevenue[];
  byCurrency: CurrencyRevenue[];
}
