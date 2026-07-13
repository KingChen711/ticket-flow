using Grpc.Core;
using TicketFlow.Contracts.Reservation;

namespace TicketFlow.Reservation.Api.Services;

/// <summary>
/// Phase 1 skeleton — always answers "available" (in-memory, no real seat state yet).
/// Real hold/lock logic (Redis TTL, chống overselling) is Phase 2's job; this exists purely
/// to prove the ordering ⇄ reservation gRPC round-trip works (Ch3).
/// </summary>
public sealed class ReservationServiceImpl : ReservationService.ReservationServiceBase
{
    public override Task<CheckSeatAvailabilityResponse> CheckSeatAvailability(
        CheckSeatAvailabilityRequest request,
        ServerCallContext context)
    {
        var response = new CheckSeatAvailabilityResponse { AllAvailable = true };
        return Task.FromResult(response);
    }
}
