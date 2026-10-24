namespace TicketFlow.Identity.Application.Users;

public sealed class RegistrationWorkflowOptions
{
    public TimeSpan RecoveryTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
