using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
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
        var values = File.ReadAllText(Path.Combine(chart, "values.yaml"));
        var messaging = File.ReadAllText(Path.Combine(
            root, "src", "BuildingBlocks", "TicketFlow.Messaging", "ServiceCollectionExtensions.cs"));
        var compose = File.ReadAllText(Path.Combine(
            root, "infrastructure", "docker", "docker-compose.infra.yml"));
        var analyticsSetup = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "Analytics",
            "TicketFlow.Analytics.Infrastructure",
            "ClickHouseTableSetup.cs"));

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
        Assert.Contains("EXPECTED_SCHEMA_VERSION", allYaml, StringComparison.Ordinal);
        Assert.Contains("ticketflow_schema_versions", allYaml, StringComparison.Ordinal);
        Assert.Contains("ticketflow-schema-versions", allYaml, StringComparison.Ordinal);
        Assert.Contains("schema_versions", allYaml, StringComparison.Ordinal);
        Assert.Contains(
            TicketFlow.Ordering.Infrastructure.OrderingMigrationRunner.SchemaVersion,
            values,
            StringComparison.Ordinal);
        Assert.Contains(
            TicketFlow.Search.Infrastructure.ElasticsearchIndexSetup.SchemaVersion,
            values,
            StringComparison.Ordinal);
        Assert.Contains(
            TicketFlow.Search.Infrastructure.ElasticsearchIndexSetup.SchemaVersionIndex,
            values,
            StringComparison.Ordinal);
        const string analyticsSchemaVersion = "phase12-analytics-20260727-v1";
        Assert.Contains(analyticsSchemaVersion, values, StringComparison.Ordinal);
        Assert.Contains(analyticsSchemaVersion, analyticsSetup, StringComparison.Ordinal);
        Assert.Contains("/health/live", allYaml, StringComparison.Ordinal);
        Assert.Contains("/health/ready", allYaml, StringComparison.Ordinal);
        Assert.Contains("SetQuorumQueue", messaging, StringComparison.Ordinal);
        Assert.Contains("hostname: rabbitmq", compose, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Init_containers_and_broker_credentials_follow_the_restricted_runtime_contract()
    {
        var root = FindRepositoryRoot();
        var chart = Path.Combine(root, "infrastructure", "k8s", "ticketflow");
        var helpers = File.ReadAllText(Path.Combine(chart, "templates", "_helpers.tpl"));
        var services = File.ReadAllText(Path.Combine(chart, "templates", "services.yaml"));
        var migrations = File.ReadAllText(Path.Combine(chart, "templates", "migrations.yaml"));
        var keycloak = File.ReadAllText(Path.Combine(chart, "templates", "keycloak.yaml"));
        var secrets = File.ReadAllText(Path.Combine(chart, "templates", "secrets.yaml"));
        var values = File.ReadAllText(Path.Combine(chart, "values.yaml"));

        Assert.Contains("ticketflow.restrictedContainerSecurityContext", helpers, StringComparison.Ordinal);
        Assert.Contains("allowPrivilegeEscalation: false", helpers, StringComparison.Ordinal);
        Assert.Contains("runAsNonRoot: true", helpers, StringComparison.Ordinal);
        Assert.Contains("ticketflow.restrictedContainerSecurityContext", services, StringComparison.Ordinal);
        Assert.Contains("ticketflow.restrictedContainerSecurityContext", migrations, StringComparison.Ordinal);
        Assert.Contains("ticketflow.restrictedContainerSecurityContext", keycloak, StringComparison.Ordinal);
        Assert.Contains("rabbitmq-{{ $name }}-password", secrets, StringComparison.Ordinal);
        Assert.Contains("TicketFlow[.].*", secrets, StringComparison.Ordinal);
        Assert.Contains("rabbitUser: ticketflow_catalog", values, StringComparison.Ordinal);
        Assert.Contains("rabbitUser: ticketflow_analytics", values, StringComparison.Ordinal);
        Assert.DoesNotContain("rabbitmq-app-password", helpers + secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("rabbitmqAppPassword", values, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Compose_chaos_and_flash_sale_checks_are_not_hidden_by_global_names_or_expected_id_filters()
    {
        var root = FindRepositoryRoot();
        var composeSources = string.Join('\n',
            File.ReadAllText(Path.Combine(root, "docker-compose.yml")),
            File.ReadAllText(Path.Combine(root, "docker-compose.e2e.yml")),
            File.ReadAllText(Path.Combine(root, "infrastructure", "docker", "docker-compose.infra.yml")),
            File.ReadAllText(Path.Combine(root, "infrastructure", "docker", "docker-compose.monitoring.yml")));
        var chaos = File.ReadAllText(Path.Combine(root, "tests", "ci", "run-chaos.sh"));
        var flashSale = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "TicketFlow.FlashSale.LoadTests",
            "Program.cs"));

        Assert.DoesNotContain("container_name:", composeSources, StringComparison.Ordinal);
        Assert.DoesNotContain("name: ticketflow-network", composeSources, StringComparison.Ordinal);
        Assert.Contains("--force-recreate", chaos, StringComparison.Ordinal);
        Assert.Contains("com.docker.compose.project", chaos, StringComparison.Ordinal);
        Assert.Contains("redis_key=", chaos, StringComparison.Ordinal);
        Assert.Contains("rabbit_queue=", chaos, StringComparison.Ordinal);
        Assert.Contains("kafka_topic=", chaos, StringComparison.Ordinal);
        Assert.Contains("keycloak_user=", chaos, StringComparison.Ordinal);
        Assert.Contains("baselineAuthorizedPaymentIds", flashSale, StringComparison.Ordinal);
        Assert.Contains("WHERE event_id = @event_id AND issuance_state = 'Issued'", flashSale, StringComparison.Ordinal);
        Assert.DoesNotContain("Where(expectedSet.Contains)", flashSale, StringComparison.Ordinal);
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

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Production_service_tokens_and_browser_origins_are_explicit_and_secret_backed()
    {
        var root = FindRepositoryRoot();
        var chart = Path.Combine(root, "infrastructure", "k8s", "ticketflow");
        var productionValues = File.ReadAllText(Path.Combine(chart, "values-production.yaml"));
        var helpers = File.ReadAllText(Path.Combine(chart, "templates", "_helpers.tpl"));
        var frontends = File.ReadAllText(Path.Combine(chart, "templates", "frontends.yaml"));
        var catalogSettings = File.ReadAllText(Path.Combine(
            root, "src", "Services", "Catalog", "TicketFlow.Catalog.Api", "appsettings.json"));
        var orderingSettings = File.ReadAllText(Path.Combine(
            root, "src", "Services", "Ordering", "TicketFlow.Ordering.Api", "appsettings.json"));
        var gatewaySettings = File.ReadAllText(Path.Combine(
            root, "src", "Gateway", "TicketFlow.Gateway", "appsettings.json"));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var customerDockerfile = File.ReadAllText(Path.Combine(
            root, "apps", "customer-web", "Dockerfile"));

        Assert.Contains("tokenEndpoint: https://", productionValues, StringComparison.Ordinal);
        Assert.Contains("serviceClientId:", productionValues, StringComparison.Ordinal);
        Assert.Contains("publicGatewayUrl: https://", productionValues, StringComparison.Ordinal);
        Assert.Contains("allowedOrigins:", productionValues, StringComparison.Ordinal);
        Assert.Contains("/organizer/api/auth", productionValues, StringComparison.Ordinal);
        Assert.Contains("/scanner/api/auth", productionValues, StringComparison.Ordinal);
        Assert.Contains("Keycloak__ServiceClientSecret", helpers, StringComparison.Ordinal);
        Assert.Contains("key: service-client-secret", helpers, StringComparison.Ordinal);
        Assert.Contains("Cors__AllowedOrigins__", helpers, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_GATEWAY_URL", frontends, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceClientSecret", catalogSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceClientSecret", orderingSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedOrigins", gatewaySettings, StringComparison.Ordinal);
        Assert.Contains("Cors__AllowedOrigins__0", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ARG NEXT_PUBLIC_GATEWAY_URL", customerDockerfile, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_GATEWAY_URL", customerDockerfile, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Service_token_registration_fails_fast_on_missing_or_insecure_production_configuration()
    {
        var missingSecret = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:TokenEndpoint"] = "https://auth.ticketflow.example/token",
                ["Keycloak:ServiceClientId"] = "ticketflow-internal",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddServiceTokenHandler(missingSecret));

        var insecureProduction = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Keycloak:TokenEndpoint"] = "http://auth.ticketflow.example/token",
                ["Keycloak:ServiceClientId"] = "ticketflow-internal",
                ["Keycloak:ServiceClientSecret"] = "external-secret",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddServiceTokenHandler(insecureProduction));

        var validProduction = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Keycloak:TokenEndpoint"] = "https://auth.ticketflow.example/token",
                ["Keycloak:ServiceClientId"] = "ticketflow-internal",
                ["Keycloak:ServiceClientSecret"] = "external-secret",
            })
            .Build();
        new ServiceCollection().AddServiceTokenHandler(validProduction);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Development_notification_stub_has_no_external_readiness_dependency()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:Provider"] = "DevelopmentStub",
                ["Delivery:HealthUrl"] = "http://notification-provider.invalid/health",
            })
            .Build();

        Assert.Null(TicketFlow.Notification.Infrastructure.ServiceCollectionExtensions
            .ResolveDeliveryHealthEndpoint(
            configuration,
            Environments.Development));
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
