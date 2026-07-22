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
