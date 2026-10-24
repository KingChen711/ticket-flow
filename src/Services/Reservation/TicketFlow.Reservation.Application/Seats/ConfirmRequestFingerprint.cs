using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TicketFlow.Reservation.Application.Seats;

internal static class ConfirmRequestFingerprint
{
    public static string Create(ConfirmSeatAllocationRequest request)
    {
        var canonicalSeats = string.Join(",", request.SeatIds.Order().Select(id => id.ToString("N")));
        var canonical = string.Join(
            "|",
            request.OrderId.ToString("N"),
            request.EventId.ToString("N"),
            request.HoldId.ToString("N"),
            request.OwnerUserId.ToString("N"),
            request.Generation.ToString(CultureInfo.InvariantCulture),
            request.PriceSnapshotVersion.ToString(CultureInfo.InvariantCulture),
            request.Amount.ToString("G29", CultureInfo.InvariantCulture),
            request.Currency.ToUpperInvariant(),
            canonicalSeats);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

internal static class ReleaseRequestFingerprint
{
    /// <summary>
    /// Deliberately excludes the seat set. The saga names the seats it believes it holds, but the
    /// durable quote is the authority — including them here would turn a harmless republish that
    /// disagrees about seats into an idempotency conflict instead of letting the ownership check
    /// decide.
    /// </summary>
    public static string Create(ReleaseSeatAllocationRequest request)
    {
        var canonical = string.Join(
            "|",
            request.OrderId.ToString("N"),
            request.EventId.ToString("N"),
            request.HoldId.ToString("N"),
            request.OwnerUserId?.ToString("N") ?? "-",
            request.Generation.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
