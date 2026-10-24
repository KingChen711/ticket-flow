namespace TicketFlow.Identity.Application.Abstractions;

/// <summary>Translated by Infrastructure so Application does not depend on EF/Npgsql exception types.</summary>
public sealed class RegistrationPersistenceConflictException(string message, Exception innerException)
    : Exception(message, innerException);
