using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TicketFlow.Web;

namespace TicketFlow.Phase12.HardeningTests.Infrastructure;

public sealed class ProductionHostingContractTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Liveness_stays_healthy_while_a_required_dependency_makes_readiness_fail()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTicketFlowWeb();
        builder.Services.AddHealthChecks().AddCheck(
            "unavailable-dependency",
            () => HealthCheckResult.Unhealthy("controlled outage"),
            tags: [ServiceCollectionExtensions.ReadinessTag]);

        await using var app = builder.Build();
        app.UseTicketFlowWeb();
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Helm_contract_has_durable_state_ordered_provisioning_and_no_latest_images()
    {
        var root = FindRepositoryRoot();
        var chart = Path.Combine(root, "infrastructure", "k8s", "ticketflow");
        var allYaml = string.Join('\n', Directory.GetFiles(chart, "*.yaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        var messaging = File.ReadAllText(Path.Combine(
            root, "src", "BuildingBlocks", "TicketFlow.Messaging", "ServiceCollectionExtensions.cs"));
        var compose = File.ReadAllText(Path.Combine(
            root, "infrastructure", "docker", "docker-compose.infra.yml"));

        Assert.DoesNotContain("image: masstransit/rabbitmq:latest", allYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("image: minio/minio:latest", allYaml, StringComparison.Ordinal);
        Assert.Contains("--appendonly yes", allYaml, StringComparison.Ordinal);
        Assert.Contains("default_queue_type = quorum", allYaml, StringComparison.Ordinal);
        Assert.Contains("KAFKA_AUTO_CREATE_TOPICS_ENABLE", allYaml, StringComparison.Ordinal);
        Assert.Contains("ticketflow-kafka-provision", allYaml, StringComparison.Ordinal);
        Assert.Contains("publishNotReadyAddresses: true", allYaml, StringComparison.Ordinal);
        Assert.Contains("retentionMs | int64", allYaml, StringComparison.Ordinal);
        Assert.Contains("KC_DB", allYaml, StringComparison.Ordinal);
        Assert.Contains("volumeClaimTemplates", allYaml, StringComparison.Ordinal);
        Assert.Contains("migration_id", allYaml, StringComparison.Ordinal);
        Assert.Contains("/health/live", allYaml, StringComparison.Ordinal);
        Assert.Contains("/health/ready", allYaml, StringComparison.Ordinal);
        Assert.Contains("SetQuorumQueue", messaging, StringComparison.Ordinal);
        Assert.Contains("hostname: rabbitmq", compose, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Production_overlay_requires_external_secrets_https_and_no_development_realm()
    {
        var root = FindRepositoryRoot();
        var productionValues = File.ReadAllText(Path.Combine(
            root, "infrastructure", "k8s", "ticketflow", "values-production.yaml"));

        Assert.Contains("create: false", productionValues, StringComparison.Ordinal);
        Assert.Contains("environment: Production", productionValues, StringComparison.Ordinal);
        Assert.Contains("virtualHost: ticketflow", productionValues, StringComparison.Ordinal);
        Assert.Contains("useSsl: true", productionValues, StringComparison.Ordinal);
        Assert.Contains("useSasl: true", productionValues, StringComparison.Ordinal);
        Assert.Contains("importDevelopmentRealm: false", productionValues, StringComparison.Ordinal);
        Assert.DoesNotContain("host.docker.internal", productionValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=ticketflow", productionValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tag: latest", productionValues, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--migrate", true)]
    [InlineData("--MIGRATE", false)]
    [InlineData("--run", false)]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Migration_mode_is_explicit_and_case_sensitive(string argument, bool expected) =>
        Assert.Equal(expected, EfMigrationRunner.IsRequested([argument]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TicketFlow.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("TicketFlow repository root was not found.");
    }
}
