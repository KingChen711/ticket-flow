using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace TicketFlow.Notification.Infrastructure;

/// <summary>
/// Same real, driver-version-specific gotcha Recommendation.Infrastructure already probed and
/// documented (Phase 6 Commit 3): MongoDB.Driver v3.x removed the implicit "CSharpLegacy" Guid
/// representation, so any Guid-typed field throws BsonSerializationException without this
/// registered up front. RegisterSerializer throws if called twice for the same type — guarded to
/// run exactly once per process, same as Recommendation's copy (no shared building block for
/// this exists yet; duplicating the ~6-line guard is cheaper than introducing one for two
/// call sites).
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
