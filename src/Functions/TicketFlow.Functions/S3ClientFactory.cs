using Amazon.S3;

namespace TicketFlow.Functions;

/// <summary>
/// LocalStack injects its own edge endpoint into every Lambda execution environment via
/// AWS_ENDPOINT_URL (confirmed by inspecting a real container's env vars while probing Commit 1)
/// — reading it explicitly here rather than trusting the SDK's own environment-variable
/// detection to be present in this exact AWSSDK.S3 version. ForcePathStyle is required: LocalStack
/// doesn't set up per-bucket virtual-hosted-style DNS the way real S3 does, so
/// http://host/bucket/key is the only addressing style that resolves from inside this network.
/// Credentials come from the same environment (AWS_ACCESS_KEY_ID etc., also LocalStack-injected)
/// via the SDK's default credential chain — nothing to configure explicitly for those.
/// </summary>
internal static class S3ClientFactory
{
    public static IAmazonS3 Create()
    {
        var config = new AmazonS3Config { ForcePathStyle = true };

        var serviceUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }

        return new AmazonS3Client(config);
    }
}
