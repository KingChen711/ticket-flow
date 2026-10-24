using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TicketFlow.Ordering.Application.Checkout;

/// <summary>
/// Durable proof that one checkout request already produced one order. Stored in the same Marten
/// transaction as the order stream, so "the order exists" and "we remember deciding to create it"
/// can never disagree — which is precisely what the old code could not guarantee: it minted a
/// fresh <c>Guid.NewGuid()</c> per attempt, so a retry after a failed publish silently created a
/// second order for the same hold.
/// </summary>
/// <remarks>
/// Keyed by the caller-supplied idempotency key scoped to the customer, so two customers cannot
/// collide on a guessable key like "checkout-1". <see cref="HoldId"/> carries its own unique index
/// (see the Marten registration) as a second, independent guard: even a client that sends a fresh
/// idempotency key for every attempt cannot turn one hold into two orders.
/// </remarks>
public sealed class CheckoutOperation
{
    public string Id { get; set; } = "";

    public Guid CustomerId { get; set; }

    public Guid HoldId { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>
    /// Hash of the semantic request. A retry that hashes the same is a replay and replays the
    /// original outcome; a retry that hashes differently is a different intent wearing the same
    /// key, and is refused rather than silently served the first order.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public static string KeyFor(Guid customerId, string idempotencyKey) =>
        $"{customerId:N}:{idempotencyKey}";
}

/// <summary>
/// Raised when one idempotency key is reused for a materially different request. Maps to 409 —
/// serving the original order instead would hand the caller an order for a hold they did not ask
/// about this time.
/// </summary>
public sealed class CheckoutConflictException(string message) : Exception(message);

internal static class CheckoutRequestFingerprint
{
    /// <summary>
    /// Covers only what the caller actually chose: which hold, how they intend to pay, and who
    /// receives the tickets. Amount, currency and seats are deliberately absent — they come from
    /// the server-side quote (Commit 3), so folding them in would turn a legitimate replay into a
    /// conflict the moment a price snapshot rolled forward between attempts.
    /// </summary>
    public static string Create(Guid customerId, Guid holdId, string paymentMethod, string customerEmail)
    {
        var canonical = string.Join(
            "|",
            customerId.ToString("N"),
            holdId.ToString("N"),
            paymentMethod.Trim().ToUpperInvariant(),
            customerEmail.Trim().ToUpperInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CreateLegacy(
        Guid customerId,
        Guid holdId,
        Guid catalogEventId,
        decimal totalAmount,
        string currency) =>
        Create(
            customerId,
            holdId,
            string.Join(
                "~",
                catalogEventId.ToString("N"),
                totalAmount.ToString("G29", CultureInfo.InvariantCulture),
                currency.ToUpperInvariant()),
            "-");
}
