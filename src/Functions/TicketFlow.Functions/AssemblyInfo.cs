using Amazon.Lambda.Core;

// One JSON serializer for every Lambda handler in this assembly.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
