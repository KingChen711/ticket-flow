using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Infrastructure;

public sealed class DevelopmentNotificationProvider(IHostEnvironment environment) : INotificationProvider
{
    private readonly ConcurrentDictionary<string, string> _references = new(StringComparer.Ordinal);

    public Task<ProviderDeliveryConfirmation> SendAsync(
        ProviderNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (environment.IsProduction())
            throw new InvalidOperationException("The development notification provider cannot run in Production.");

        var reference = _references.GetOrAdd(
            notification.IdempotencyKey,
            key => $"dev-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant()}");
        return Task.FromResult(new ProviderDeliveryConfirmation(reference));
    }
}

public sealed class HttpNotificationProvider(HttpClient client) : INotificationProvider
{
    public async Task<ProviderDeliveryConfirmation> SendAsync(
        ProviderNotification notification,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = JsonContent.Create(new
            {
                recipient = notification.Recipient,
                template = notification.Template,
                subject = notification.Subject,
                body = notification.Body,
            }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", notification.IdempotencyKey);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var confirmation = await response.Content.ReadFromJsonAsync<HttpProviderResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The delivery provider returned an empty response.");
        var reference = confirmation.MessageId ?? confirmation.ReferenceId;
        return string.IsNullOrWhiteSpace(reference)
            ? throw new InvalidOperationException("The delivery provider response has no message/reference id.")
            : new ProviderDeliveryConfirmation(reference);
    }

    private sealed record HttpProviderResponse(
        [property: JsonPropertyName("messageId")] string? MessageId,
        [property: JsonPropertyName("referenceId")] string? ReferenceId);
}
