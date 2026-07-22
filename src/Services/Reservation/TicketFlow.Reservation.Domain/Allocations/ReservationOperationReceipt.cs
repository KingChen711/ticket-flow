using System.Text.Json;

namespace TicketFlow.Reservation.Domain.Allocations;

public sealed class ReservationOperationReceipt
{
    private ReservationOperationReceipt()
    {
    }

    public Guid Id { get; private set; }

    public string OperationType { get; private set; } = string.Empty;

    public string BusinessKey { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public string? Outcome { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsPending => CompletedAt is null && Outcome is null;

    public static ReservationOperationReceipt StartConfirm(
        string businessKey,
        string requestFingerprint,
        DateTimeOffset startedAt)
    {
        ValidateIdentity(businessKey, requestFingerprint);

        return new ReservationOperationReceipt
        {
            Id = Guid.NewGuid(),
            OperationType = OperationTypes.ConfirmSeats,
            BusinessKey = businessKey.Trim(),
            RequestFingerprint = requestFingerprint.Trim(),
            Outcome = null,
            CreatedAt = startedAt,
            CompletedAt = null
        };
    }

    public bool HasFingerprint(string requestFingerprint) =>
        string.Equals(RequestFingerprint, requestFingerprint, StringComparison.Ordinal);

    public void CompletePendingConfirm(
        string requestFingerprint,
        ConfirmSeatsReceiptSnapshot snapshot,
        DateTimeOffset completedAt)
    {
        EnsureConfirmFingerprint(requestFingerprint);

        if (!IsPending)
        {
            _ = ReplayConfirm(requestFingerprint);
            return;
        }

        var normalizedSnapshot = snapshot with { AllocationReference = Id };
        Outcome = JsonSerializer.Serialize(normalizedSnapshot);
        CompletedAt = completedAt;
    }

    public ConfirmSeatsReceiptSnapshot ReplayConfirm(string requestFingerprint)
    {
        EnsureConfirmFingerprint(requestFingerprint);

        if (IsPending || string.IsNullOrWhiteSpace(Outcome) || CompletedAt is null)
        {
            throw new InvalidOperationException("The confirm-seats operation is still pending.");
        }

        return JsonSerializer.Deserialize<ConfirmSeatsReceiptSnapshot>(Outcome)
            ?? throw new InvalidOperationException("The stored confirm-seats outcome is invalid.");
    }

    public static string ConfirmBusinessKey(Guid orderId, Guid holdId)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        if (holdId == Guid.Empty)
        {
            throw new ArgumentException("Hold id is required.", nameof(holdId));
        }

        return $"{orderId:N}:{holdId:N}";
    }

    public static string ConfirmBusinessKey(string? operationKey, Guid orderId, Guid holdId) =>
        string.IsNullOrWhiteSpace(operationKey)
            ? ConfirmBusinessKey(orderId, holdId)
            : operationKey.Trim();

    private static void ValidateIdentity(string businessKey, string requestFingerprint)
    {
        if (string.IsNullOrWhiteSpace(businessKey))
        {
            throw new ArgumentException("Business key is required.", nameof(businessKey));
        }

        if (string.IsNullOrWhiteSpace(requestFingerprint))
        {
            throw new ArgumentException("Request fingerprint is required.", nameof(requestFingerprint));
        }
    }

    private void EnsureConfirmFingerprint(string requestFingerprint)
    {
        if (!string.Equals(OperationType, OperationTypes.ConfirmSeats, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The receipt is not a confirm-seats operation.");
        }

        if (!HasFingerprint(requestFingerprint))
        {
            throw new ReservationOperationFingerprintMismatchException(
                OperationType,
                BusinessKey,
                RequestFingerprint,
                requestFingerprint);
        }
    }

    public static class OperationTypes
    {
        public const string ConfirmSeats = "ConfirmSeats";
    }
}

public sealed record ConfirmSeatsReceiptSnapshot(
    bool Succeeded,
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    Guid OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> SeatIds,
    Guid AllocationReference,
    long PriceSnapshotVersion,
    decimal Amount,
    string Currency,
    DateTimeOffset DecidedAt,
    string? FailureCode = null,
    string? FailureReason = null);

public sealed class ReservationOperationFingerprintMismatchException : InvalidOperationException
{
    public ReservationOperationFingerprintMismatchException(
        string operationType,
        string businessKey,
        string expectedFingerprint,
        string actualFingerprint)
        : base($"The {operationType} operation '{businessKey}' was already claimed with a different request fingerprint.")
    {
        OperationType = operationType;
        BusinessKey = businessKey;
        ExpectedFingerprint = expectedFingerprint;
        ActualFingerprint = actualFingerprint;
    }

    public string OperationType { get; }

    public string BusinessKey { get; }

    public string ExpectedFingerprint { get; }

    public string ActualFingerprint { get; }
}
