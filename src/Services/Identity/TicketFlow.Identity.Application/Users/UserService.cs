using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Application.Users;

public sealed class UserService(
    IUserRepository userRepository,
    IKeycloakAdminClient keycloakAdminClient,
    IUnitOfWork unitOfWork,
    RegistrationWorkflowOptions workflowOptions,
    TimeProvider timeProvider,
    ILogger<UserService> logger)
{
    private const int PersistenceConflictRetries = 4;

    public async Task<UserResponse> RegisterAsync(
        RegisterUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var email = Email.Create(request.Email);
        RegistrationPasswordPolicy.Validate(request.Password, email.Value);

        if (request.RegistrationKey == Guid.Empty)
            throw new ArgumentException("RegistrationKey must not be empty.", nameof(request));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RegisterAttemptAsync(request, email, cancellationToken);
            }
            catch (RegistrationPersistenceConflictException) when (attempt < PersistenceConflictRetries)
            {
                // Another request with this key/email moved the workflow. Drop stale EF state,
                // reload its durable checkpoint, and decide again instead of repeating a blind
                // insert/update.
                unitOfWork.DiscardChanges();
            }
        }
    }

    public async Task<UserResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);
        return user is null ? null : Map(user);
    }

    public async Task<IReadOnlyList<UserResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await userRepository.ListAsync(cancellationToken);
        return users.Select(Map).ToList();
    }

    private async Task<UserResponse> RegisterAttemptAsync(
        RegisterUserRequest request,
        Email email,
        CancellationToken cancellationToken)
    {
        var registration = await GetOrStartRegistrationAsync(request, email, cancellationToken);
        registration.EnsureSameIntent(email.Value, request.DisplayName);

        if (registration.Status == UserRegistrationStatus.Completed)
            return await LoadCompletedUserAsync(registration, cancellationToken);

        if (registration.Status == UserRegistrationStatus.CompensationPending)
            registration = await FinishPendingCompensationAsync(registration);

        if (registration.KeycloakUserId is { } checkpointedUserId)
        {
            var alreadySaved = await userRepository.GetByIdAsync(checkpointedUserId, cancellationToken);
            if (alreadySaved is not null)
            {
                registration.Complete(timeProvider.GetUtcNow());
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Map(alreadySaved);
            }
        }

        var keycloakUserId = registration.KeycloakUserId;

        // A previous request may have lost the POST response after Keycloak committed. Only a
        // durable CreateAttemptedAt permits adopting the exact normalized username/email; a new
        // registration never takes over an unrelated pre-existing Keycloak account.
        if (keycloakUserId is null && registration.CreateAttemptedAt is not null)
            keycloakUserId = await FindPreviouslyAttemptedUserAsync(
                registration.NormalizedEmail,
                registration.Id);

        if (keycloakUserId is null)
        {
            registration.MarkCreateAttempted(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);

            try
            {
                keycloakUserId = await keycloakAdminClient.CreateUserAsync(
                    registration.NormalizedEmail,
                    registration.DisplayName,
                    registration.Id,
                    cancellationToken);
            }
            catch (KeycloakUserAlreadyExistsException)
            {
                throw;
            }
            catch (Exception createError)
            {
                keycloakUserId = await TryRecoverUncertainCreateAsync(
                    registration.NormalizedEmail,
                    registration.Id,
                    createError);

                if (keycloakUserId is null)
                    ExceptionDispatchInfo.Capture(createError).Throw();
            }
        }

        try
        {
            if (registration.KeycloakUserId is null)
            {
                registration.RecordKeycloakUser(keycloakUserId.Value, timeProvider.GetUtcNow());

                // Use an independent token if the caller vanished after Keycloak returned. This
                // tiny checkpoint prevents the next retry from creating a second remote user.
                await SaveCheckpointAsync(cancellationToken);
            }

            await keycloakAdminClient.ConfigureUserAsync(
                keycloakUserId.Value,
                request.Password,
                Role.Customer,
                cancellationToken);

            var user = await userRepository.GetByIdAsync(keycloakUserId.Value, cancellationToken);
            if (user is null)
            {
                user = User.Register(
                    keycloakUserId.Value,
                    email,
                    registration.DisplayName,
                    Role.Customer);
                userRepository.Add(user);
            }

            registration.Complete(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Map(user);
        }
        catch (RegistrationPersistenceConflictException)
        {
            // Usually the same registration finishing concurrently. The outer loop reloads the
            // winner; deleting its Keycloak user here would corrupt a successful registration.
            throw;
        }
        catch (Exception originalError)
        {
            await CompensateAndRethrowAsync(registration.Id, keycloakUserId.Value, originalError);
            throw; // unreachable: ExceptionDispatchInfo below preserves the original stack.
        }
    }

    private async Task<UserRegistration> GetOrStartRegistrationAsync(
        RegisterUserRequest request,
        Email email,
        CancellationToken cancellationToken)
    {
        var registration = await userRepository.GetRegistrationByKeyAsync(
            request.RegistrationKey,
            cancellationToken);

        if (registration is not null)
            return registration;

        if (await userRepository.GetByEmailAsync(email.Value, cancellationToken) is not null)
            throw new InvalidOperationException($"Email '{email.Value}' is already registered.");

        var emailRegistration = await userRepository.GetRegistrationByEmailAsync(email.Value, cancellationToken);
        if (emailRegistration is not null)
        {
            throw new InvalidOperationException(
                $"Email '{email.Value}' already has a registration under another registration key.");
        }

        registration = UserRegistration.Start(
            request.RegistrationKey,
            email.Value,
            request.DisplayName,
            timeProvider.GetUtcNow());
        userRepository.Add(registration);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return registration;
    }

    private async Task<UserResponse> LoadCompletedUserAsync(
        UserRegistration registration,
        CancellationToken cancellationToken)
    {
        var userId = registration.KeycloakUserId
                     ?? throw new InvalidOperationException("Completed registration has no Keycloak user id.");
        var user = await userRepository.GetByIdAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("Completed registration has no local user profile.");
        return Map(user);
    }

    private async Task<Guid?> FindPreviouslyAttemptedUserAsync(string normalizedEmail, Guid registrationKey)
    {
        using var recovery = CreateRecoveryToken();
        return await keycloakAdminClient.FindUserByNormalizedEmailAsync(
            normalizedEmail,
            registrationKey,
            recovery.Token);
    }

    private async Task<Guid?> TryRecoverUncertainCreateAsync(
        string normalizedEmail,
        Guid registrationKey,
        Exception originalError)
    {
        try
        {
            using var recovery = CreateRecoveryToken();
            return await keycloakAdminClient.FindUserByNormalizedEmailAsync(
                normalizedEmail,
                registrationKey,
                recovery.Token);
        }
        catch (Exception recoveryError)
        {
            logger.LogWarning(
                recoveryError,
                "Could not reconcile uncertain Keycloak create for {Email}; preserving original {ErrorType}",
                normalizedEmail,
                originalError.GetType().Name);
            return null;
        }
    }

    private async Task SaveCheckpointAsync(CancellationToken callerToken)
    {
        if (!callerToken.IsCancellationRequested)
        {
            await unitOfWork.SaveChangesAsync(callerToken);
            return;
        }

        using var recovery = CreateRecoveryToken();
        await unitOfWork.SaveChangesAsync(recovery.Token);
    }

    private async Task<UserRegistration> FinishPendingCompensationAsync(UserRegistration registration)
    {
        try
        {
            using var cleanup = CreateRecoveryToken();
            if (registration.KeycloakUserId is { } userId)
                await keycloakAdminClient.DeleteUserAsync(userId, cleanup.Token);

            registration.ResetAfterCompensation(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cleanup.Token);
            return registration;
        }
        catch (Exception cleanupError)
        {
            throw new InvalidOperationException(
                "Registration compensation is still pending; retry after Keycloak is available.",
                cleanupError);
        }
    }

    private async Task CompensateAndRethrowAsync(
        Guid registrationKey,
        Guid keycloakUserId,
        Exception originalError)
    {
        unitOfWork.DiscardChanges();

        try
        {
            using var cleanup = CreateRecoveryToken();
            await keycloakAdminClient.DeleteUserAsync(keycloakUserId, cleanup.Token);

            var registration = await userRepository.GetRegistrationByKeyAsync(registrationKey, cleanup.Token);
            if (registration is not null)
            {
                registration.ResetAfterCompensation(timeProvider.GetUtcNow());
                await unitOfWork.SaveChangesAsync(cleanup.Token);
            }
        }
        catch (Exception cleanupError)
        {
            logger.LogError(
                cleanupError,
                "Keycloak cleanup failed for registration {RegistrationKey}; original error remains primary",
                registrationKey);

            unitOfWork.DiscardChanges();
            try
            {
                using var checkpoint = CreateRecoveryToken();
                var registration = await userRepository.GetRegistrationByKeyAsync(registrationKey, checkpoint.Token);
                if (registration is not null)
                {
                    registration.MarkCompensationPending(cleanupError.Message, timeProvider.GetUtcNow());
                    await unitOfWork.SaveChangesAsync(checkpoint.Token);
                }
            }
            catch (Exception checkpointError)
            {
                logger.LogError(
                    checkpointError,
                    "Could not persist CompensationPending for registration {RegistrationKey}",
                    registrationKey);
            }
        }

        ExceptionDispatchInfo.Capture(originalError).Throw();
    }

    private CancellationTokenSource CreateRecoveryToken() =>
        new(workflowOptions.RecoveryTimeout);

    private static UserResponse Map(User user) => new(
        user.Id,
        user.Email.Value,
        user.DisplayName,
        user.Roles.Select(role => role.ToString()).ToList());
}
