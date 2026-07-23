using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Application.Abstractions;
using TicketFlow.Notification.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Workflows;

public sealed class NotificationDeliveryOutboxTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("DevelopmentStub", null, null)]
    [InlineData("Http", "https://provider.example/", null)]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Production_never_falls_back_to_stub(string? provider, string? baseUrl, string? apiKey)
    {
        var values = new Dictionary<string, string?>
        {
            ["Delivery:Provider"] = provider,
            ["Delivery:BaseUrl"] = baseUrl,
            ["Delivery:ApiKey"] = apiKey,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() =>
            ServiceCollectionExtensions.ValidateDeliveryConfiguration(configuration, "Production"));
    }

    [Theory]
    [InlineData("Docker")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Production_like_environment_never_falls_back_to_stub(string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Delivery:Provider"] = "DevelopmentStub" }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            ServiceCollectionExtensions.ValidateDeliveryConfiguration(configuration, environmentName));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Testing")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Development_and_test_are_the_only_stub_environments(string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        Assert.Equal(
            "DevelopmentStub",
            ServiceCollectionExtensions.ValidateDeliveryConfiguration(configuration, environmentName));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Http_provider_sends_stable_idempotency_header_and_requires_reference_id()
    {
        var handler = new RecordingProviderHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.example/") };
        var provider = new HttpNotificationProvider(client);

        var confirmation = await provider.SendAsync(new ProviderNotification(
            "delivery-key-42",
            "buyer@example.com",
            "event-cancelled",
            "Cancelled",
            "Your event was cancelled"));

        Assert.Equal("provider-message-42", confirmation.ProviderReference);
        Assert.Equal("delivery-key-42", handler.IdempotencyKey);
        Assert.Contains("buyer@example.com", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Http_provider_rejects_success_response_without_provider_reference()
    {
        var handler = new RecordingProviderHandler { ResponseBody = "{}" };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.example/") };
        var provider = new HttpNotificationProvider(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SendAsync(new ProviderNotification(
            "delivery-key-43",
            "buyer@example.com",
            "event-cancelled",
            "Cancelled",
            "Your event was cancelled")));
    }

    private sealed class RecordingProviderHandler : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string ResponseBody { get; init; } = "{\"messageId\":\"provider-message-42\"}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class MongoNotificationDeliveryOutboxTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hard_kill_after_provider_acceptance_waits_for_lease_then_reuses_same_provider_key()
    {
        var deliveryId = $"hard-kill-after-send:{Guid.NewGuid():N}";
        var store = new MongoNotificationDeliveryStore(environment.ReminderDatabase);
        var provider = new IdempotentFakeProvider();
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var request = new NotificationDeliveryRequest(
            deliveryId,
            deliveryId,
            "buyer@example.com",
            "upcoming-event-reminder",
            "Reminder",
            "Starts tomorrow");

        var pending = await store.EnsureCreatedAsync(request, now);
        var leased = await store.TryAcquireAsync(deliveryId, now, now.AddMinutes(2));
        Assert.NotNull(leased);
        await provider.SendAsync(new ProviderNotification(
            pending.IdempotencyKey,
            pending.Recipient,
            pending.Template,
            pending.Subject,
            pending.Body));
        // Simulated process death: no MarkDelivered/MarkFailed write follows provider acceptance.

        var recovered = new NotificationDeliveryService(store, provider, time);
        await Assert.ThrowsAsync<NotificationDeliveryInProgressException>(() => recovered.DeliverAsync(request));
        Assert.Equal(1, provider.CallCount);

        time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        var delivered = await recovered.DeliverAsync(request);
        Assert.Equal(NotificationDeliveryState.Delivered, delivered.State);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, provider.AcceptedCount);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Reminder_replay_keeps_first_durable_payload_when_event_projection_changes()
    {
        var deliveryId = $"rescheduled-reminder:{Guid.NewGuid():N}";
        var store = new MongoNotificationDeliveryStore(environment.ReminderDatabase);
        var original = new NotificationDeliveryRequest(
            deliveryId,
            deliveryId,
            "buyer@example.com",
            "upcoming-event-reminder",
            "Original schedule",
            "Starts tomorrow");
        var changedProjection = original with
        {
            Subject = "Rescheduled event",
            Body = "Starts next week",
        };

        await store.EnsureCreatedAsync(original, DateTimeOffset.UtcNow);
        var replay = await store.EnsureCreatedAsync(changedProjection, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(original.Subject, replay.Subject);
        Assert.Equal(original.Body, replay.Body);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Crash_after_provider_acceptance_replays_same_key_and_converges_to_one_business_delivery()
    {
        var deliveryId = $"crash-after-send:{Guid.NewGuid():N}";
        var durableStore = new MongoNotificationDeliveryStore(environment.ReminderDatabase);
        var provider = new IdempotentFakeProvider();
        var crashStore = new FailFirstDeliveredWriteStore(durableStore);
        var crashing = new NotificationDeliveryService(crashStore, provider, TimeProvider.System);
        var request = new NotificationDeliveryRequest(
            deliveryId,
            deliveryId,
            "buyer@example.com",
            "upcoming-event-reminder",
            "Reminder",
            "Starts tomorrow");

        await Assert.ThrowsAsync<IOException>(() => crashing.DeliverAsync(request));
        var failed = await durableStore.GetAsync(deliveryId);
        Assert.NotNull(failed);
        Assert.Equal(NotificationDeliveryState.Failed, failed.State);

        var retrying = new NotificationDeliveryService(durableStore, provider, TimeProvider.System);
        var delivered = await retrying.DeliverAsync(request);
        Assert.Equal(NotificationDeliveryState.Delivered, delivered.State);
        Assert.NotNull(delivered.ProviderReference);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, provider.AcceptedCount);

        var replay = await retrying.DeliverAsync(request);
        Assert.Equal(NotificationDeliveryState.Delivered, replay.State);
        Assert.Equal(2, provider.CallCount);
    }

    private sealed class IdempotentFakeProvider : INotificationProvider
    {
        private readonly ConcurrentDictionary<string, string> _accepted = new(StringComparer.Ordinal);
        private int _calls;
        public int CallCount => _calls;
        public int AcceptedCount => _accepted.Count;

        public Task<ProviderDeliveryConfirmation> SendAsync(
            ProviderNotification notification,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var reference = _accepted.GetOrAdd(
                notification.IdempotencyKey,
                _ => $"fake-{Guid.NewGuid():N}");
            return Task.FromResult(new ProviderDeliveryConfirmation(reference));
        }
    }

    private sealed class FailFirstDeliveredWriteStore(INotificationDeliveryStore inner)
        : INotificationDeliveryStore
    {
        private int _failed;

        public Task<NotificationDeliveryAttempt> EnsureCreatedAsync(
            NotificationDeliveryRequest request,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default) =>
            inner.EnsureCreatedAsync(request, createdAt, cancellationToken);

        public Task<NotificationDeliveryAttempt?> GetAsync(
            string deliveryId,
            CancellationToken cancellationToken = default) => inner.GetAsync(deliveryId, cancellationToken);

        public Task<NotificationDeliveryAttempt?> TryAcquireAsync(
            string deliveryId,
            DateTimeOffset now,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken = default) =>
            inner.TryAcquireAsync(deliveryId, now, leaseUntil, cancellationToken);

        public Task MarkDeliveredAsync(
            string deliveryId,
            string providerReference,
            DateTimeOffset deliveredAt,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("Injected crash after provider acceptance.");
            return inner.MarkDeliveredAsync(deliveryId, providerReference, deliveredAt, cancellationToken);
        }

        public Task MarkFailedAsync(
            string deliveryId,
            string error,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken = default) =>
            inner.MarkFailedAsync(deliveryId, error, failedAt, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
