using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace TicketFlow.Recommendation.Infrastructure;

/// <summary>
/// Real, driver-version-specific gotcha confirmed via a probe against a live mongo:7 container
/// before writing this: MongoDB.Driver v3.x removed the old implicit "CSharpLegacy" Guid
/// representation. Without registering GuidRepresentation.Standard (the MongoDB-canonical UUID
/// subtype 4) up front, any operation touching a Guid-typed field throws
/// "BsonSerializationException: GuidSerializer cannot serialize a Guid when GuidRepresentation is
/// Unspecified". RegisterSerializer throws if called twice for the same type, so this guards it
/// to run exactly once per process.
/// </summary>
public static class MongoSerializationConfig
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        return true;
    });

    public static void EnsureConfigured() => _ = Registered.Value;
}
