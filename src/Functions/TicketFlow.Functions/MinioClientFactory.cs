using Minio;

namespace TicketFlow.Functions;

/// <summary>
/// Unlike S3ClientFactory (Commit 2), LocalStack injects nothing useful here — MinIO isn't an
/// AWS service, so there's no AWS_ENDPOINT_URL/credential-chain shortcut. Endpoint/credentials/
/// bucket come from plain environment variables set on the function definition itself
/// (`--environment Variables={...}` in the init deploy script) — the same Minio:Endpoint/
/// AccessKey/SecretKey/BucketName values Ticketing.Infrastructure's ServiceCollectionExtensions
/// already reads from appsettings, just via env vars instead of IConfiguration since a Lambda
/// class library has no ASP.NET Core configuration host.
/// </summary>
internal static class MinioClientFactory
{
    public static IMinioClient Create()
    {
        var endpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT")
            ?? throw new InvalidOperationException("Environment variable 'MINIO_ENDPOINT' was not set.");
        var accessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY")
            ?? throw new InvalidOperationException("Environment variable 'MINIO_ACCESS_KEY' was not set.");
        var secretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY")
            ?? throw new InvalidOperationException("Environment variable 'MINIO_SECRET_KEY' was not set.");

        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(false)
            .Build();
    }
}
