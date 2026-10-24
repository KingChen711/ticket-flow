using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Application.Users;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Phase12.HardeningTests.Identity;

public sealed class DurableRegistrationWorkflowTests
{
    private const string StrongPassword = "Str0ng!TicketFlow";

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Lost_create_response_is_reconciled_and_same_key_returns_the_completed_user()
    {
        var remoteUserId = Guid.NewGuid();
        var repository = new FakeUserRepository();
        var keycloak = new FakeKeycloakAdminClient
        {
            Create = (_, _, _, _) => throw new HttpRequestException("response lost"),
            Find = (_, _, _) => Task.FromResult<Guid?>(remoteUserId),
        };
        var service = CreateService(repository, keycloak, new FakeUnitOfWork());
        var request = new RegisterUserRequest(
            " Buyer@Example.com ", "Buyer", StrongPassword, Guid.NewGuid());

        var first = await service.RegisterAsync(request);
        var replay = await service.RegisterAsync(request);

        Assert.Equal(remoteUserId, first.Id);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Email, replay.Email);
        Assert.Equal(first.DisplayName, replay.DisplayName);
        Assert.Equal(first.Roles, replay.Roles);
        Assert.Equal(1, keycloak.CreateCalls);
        Assert.Equal(1, keycloak.FindCalls);
        Assert.Equal(1, keycloak.ConfigureCalls);
        Assert.Equal(0, keycloak.DeleteCalls);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal("buyer@example.com", registration.NormalizedEmail);
        Assert.Equal(UserRegistrationStatus.Completed, registration.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Caller_cancellation_after_remote_create_uses_an_independent_cleanup_token()
    {
        using var caller = new CancellationTokenSource();
        var repository = new FakeUserRepository();
        var keycloak = new FakeKeycloakAdminClient
        {
            Create = (_, _, _, _) =>
            {
                caller.Cancel();
                return Task.FromResult(Guid.NewGuid());
            },
            Configure = (_, _, _, token) => Task.FromCanceled(token),
        };
        var service = CreateService(repository, keycloak, new FakeUnitOfWork());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterAsync(
            new RegisterUserRequest("cancel@example.com", "Cancelled", StrongPassword, Guid.NewGuid()),
            caller.Token));

        Assert.Equal(1, keycloak.DeleteCalls);
        Assert.False(keycloak.DeleteTokenWasCancelled);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal(UserRegistrationStatus.Pending, registration.Status);
        Assert.Null(registration.KeycloakUserId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cancellation_during_uncertain_create_reconciles_with_an_independent_token()
    {
        using var caller = new CancellationTokenSource();
        var repository = new FakeUserRepository();
        var keycloak = new FakeKeycloakAdminClient
        {
            Create = (_, _, _, token) =>
            {
                caller.Cancel();
                return Task.FromCanceled<Guid>(token);
            },
        };
        var service = CreateService(repository, keycloak, new FakeUnitOfWork());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterAsync(
            new RegisterUserRequest("uncertain@example.com", "Uncertain", StrongPassword, Guid.NewGuid()),
            caller.Token));

        Assert.Equal(1, keycloak.FindCalls);
        Assert.False(keycloak.FindTokenWasCancelled);
        Assert.Equal(0, keycloak.DeleteCalls);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal(UserRegistrationStatus.Pending, registration.Status);
        Assert.NotNull(registration.CreateAttemptedAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cancellation_after_Keycloak_configuration_compensates_before_returning()
    {
        using var caller = new CancellationTokenSource();
        var repository = new FakeUserRepository();
        var keycloak = new FakeKeycloakAdminClient
        {
            Configure = (_, _, _, _) =>
            {
                caller.Cancel();
                return Task.CompletedTask;
            },
        };
        var service = CreateService(repository, keycloak, new FakeUnitOfWork
        {
            BeforeCancelledSave = repository.RollBackUncommittedFinalSave,
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterAsync(
            new RegisterUserRequest("commit-cancel@example.com", "Commit Cancel", StrongPassword, Guid.NewGuid()),
            caller.Token));

        Assert.Equal(1, keycloak.DeleteCalls);
        Assert.False(keycloak.DeleteTokenWasCancelled);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal(UserRegistrationStatus.Pending, registration.Status);
        Assert.Null(registration.KeycloakUserId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cleanup_failure_records_compensation_pending_without_masking_database_error()
    {
        var databaseError = new RegistrationDatabaseUnavailableException("local save failed");
        var repository = new FakeUserRepository();
        var unitOfWork = new FakeUnitOfWork
        {
            Failure = call => call == 4 ? databaseError : null,
            BeforeFailure = call =>
            {
                if (call == 4)
                    repository.RollBackUncommittedFinalSave();
            },
        };
        var keycloak = new FakeKeycloakAdminClient
        {
            Delete = (_, _) => throw new HttpRequestException("Keycloak unavailable"),
        };
        var service = CreateService(repository, keycloak, unitOfWork);

        var thrown = await Assert.ThrowsAsync<RegistrationDatabaseUnavailableException>(() =>
            service.RegisterAsync(new RegisterUserRequest(
                "db-failure@example.com", "DB Failure", StrongPassword, Guid.NewGuid())));

        Assert.Same(databaseError, thrown);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal(UserRegistrationStatus.CompensationPending, registration.Status);
        Assert.Contains("Keycloak unavailable", registration.LastError);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Lost_acknowledgement_after_local_commit_must_not_delete_the_successful_Keycloak_user()
    {
        var ambiguous = new RegistrationDatabaseUnavailableException(
            "connection dropped after COMMIT");
        var repository = new FakeUserRepository();
        var unitOfWork = new FakeUnitOfWork
        {
            // The repository mutations remain visible: this models COMMIT succeeding before the
            // database acknowledgement is lost.
            Failure = call => call == 4 ? ambiguous : null,
        };
        var keycloak = new FakeKeycloakAdminClient();
        var service = CreateService(repository, keycloak, unitOfWork);

        var registered = await service.RegisterAsync(new RegisterUserRequest(
            "committed@example.com",
            "Committed",
            StrongPassword,
            Guid.NewGuid()));

        Assert.Equal("committed@example.com", registered.Email);
        Assert.Equal(0, keycloak.DeleteCalls);
        var registration = Assert.Single(repository.Registrations);
        Assert.Equal(UserRegistrationStatus.Completed, registration.Status);
        Assert.Equal(registered.Id, registration.KeycloakUserId);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoNumber!Password")]
    [InlineData("NoSpecial1Password")]
    [InlineData("buyer-Str0ng!Password")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Weak_passwords_are_rejected_before_Keycloak(string password)
    {
        Assert.Throws<ArgumentException>(() =>
            RegistrationPasswordPolicy.Validate(password, "buyer@example.com"));
    }

    private static UserService CreateService(
        FakeUserRepository repository,
        FakeKeycloakAdminClient keycloak,
        FakeUnitOfWork unitOfWork) =>
        new(
            repository,
            keycloak,
            unitOfWork,
            new RegistrationWorkflowOptions { RecoveryTimeout = TimeSpan.FromSeconds(2) },
            TimeProvider.System,
            NullLogger<UserService>.Instance);

    private sealed class FakeUserRepository : IUserRepository
    {
        private readonly Dictionary<Guid, User> _users = [];
        private readonly Dictionary<Guid, UserRegistration> _registrations = [];

        public IReadOnlyCollection<UserRegistration> Registrations => _registrations.Values;

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_users.GetValueOrDefault(id));

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(_users.Values.SingleOrDefault(user => user.Email.Value == email));

        public Task<UserRegistration?> GetRegistrationByKeyAsync(
            Guid registrationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_registrations.GetValueOrDefault(registrationKey));

        public Task<UserRegistration?> GetRegistrationByEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_registrations.Values.SingleOrDefault(
                registration => registration.NormalizedEmail == normalizedEmail));

        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(_users.Values.ToList());

        public void Add(User user) => _users[user.Id] = user;

        public void Add(UserRegistration registration) => _registrations[registration.Id] = registration;

        public void RollBackUncommittedFinalSave()
        {
            _users.Clear();
            foreach (var registration in _registrations.Values)
            {
                if (registration.Status != UserRegistrationStatus.Completed)
                    continue;

                var keycloakUserId = registration.KeycloakUserId;
                registration.ResetAfterCompensation(DateTimeOffset.UtcNow);
                if (keycloakUserId is { } userId)
                    registration.RecordKeycloakUser(userId, DateTimeOffset.UtcNow);
            }
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        private int _saveCalls;
        public Func<int, Exception?>? Failure { get; init; }
        public Action<int>? BeforeFailure { get; init; }
        public Action? BeforeCancelledSave { get; init; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                BeforeCancelledSave?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _saveCalls);
            var failure = Failure?.Invoke(call);
            if (failure is not null)
                BeforeFailure?.Invoke(call);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public void DiscardChanges() { }
    }

    private sealed class FakeKeycloakAdminClient : IKeycloakAdminClient
    {
        public Func<string, Guid, CancellationToken, Task<Guid?>> Find { get; init; } =
            (_, _, _) => Task.FromResult<Guid?>(null);
        public Func<string, string, Guid, CancellationToken, Task<Guid>> Create { get; init; } =
            (_, _, _, _) => Task.FromResult(Guid.NewGuid());
        public Func<Guid, string, Role, CancellationToken, Task> Configure { get; init; } =
            (_, _, _, _) => Task.CompletedTask;
        public Func<Guid, CancellationToken, Task> Delete { get; init; } =
            (_, _) => Task.CompletedTask;

        public int FindCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int ConfigureCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool DeleteTokenWasCancelled { get; private set; }
        public bool FindTokenWasCancelled { get; private set; }

        public Task<Guid?> FindUserByNormalizedEmailAsync(
            string normalizedEmail,
            Guid registrationKey,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            FindTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Find(normalizedEmail, registrationKey, cancellationToken);
        }

        public Task<Guid> CreateUserAsync(
            string normalizedEmail,
            string displayName,
            Guid registrationKey,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Create(normalizedEmail, displayName, registrationKey, cancellationToken);
        }

        public Task ConfigureUserAsync(
            Guid keycloakUserId,
            string password,
            Role role,
            CancellationToken cancellationToken = default)
        {
            ConfigureCalls++;
            return Configure(keycloakUserId, password, role, cancellationToken);
        }

        public Task DeleteUserAsync(Guid keycloakUserId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            DeleteTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Delete(keycloakUserId, cancellationToken);
        }
    }

    private sealed class RegistrationDatabaseUnavailableException(string message) : Exception(message);
}
