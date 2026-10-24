namespace TicketFlow.Catalog.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Drop unsaved tracked mutations before recording a workflow failure.</summary>
    void DiscardChanges() { }
}
