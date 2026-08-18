using System.Text.Json;
using CrossRagfair.Contracts;

namespace CrossRagfair.Core;

public sealed class HubState
{
    public Dictionary<string, SharedOffer> Offers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, Reservation> Reservations { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PeerLease> Peers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, StoredResult> Idempotency { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, StoredOriginEvent> OriginEvents { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> AcknowledgedOriginEvents { get; init; } = new(StringComparer.Ordinal);
    public List<ProjectionChange> ProjectionChanges { get; init; } = [];

    public void Apply(HubEventEnvelope entry)
    {
        switch (entry.EventType)
        {
            case HubEventTypes.PeerRegistered:
                var peer = entry.Payload.Deserialize<PeerLease>(JsonDefaults.Options)!;
                Peers[peer.ServerId] = peer;
                break;
            case HubEventTypes.OfferPublished:
            case HubEventTypes.OfferChanged:
                var offer = entry.Payload.Deserialize<SharedOffer>(JsonDefaults.Options)!;
                Offers[offer.GlobalOfferId] = offer;
                ProjectionChanges.Add(new(entry.Sequence, entry.EventType, offer));
                break;
            case HubEventTypes.ProjectionChanged:
                var projection = entry.Payload.Deserialize<SharedOffer>(JsonDefaults.Options)!;
                ProjectionChanges.Add(new(entry.Sequence, entry.EventType, projection));
                break;
            case HubEventTypes.ReservationChanged:
                var reservation = entry.Payload.Deserialize<Reservation>(JsonDefaults.Options)!;
                Reservations[reservation.TransactionId] = reservation;
                break;
            case HubEventTypes.IdempotencyRecorded:
                var result = entry.Payload.Deserialize<StoredResult>(JsonDefaults.Options)!;
                Idempotency[result.Key] = result;
                break;
            case HubEventTypes.OriginEventAdded:
                var originEvent = entry.Payload.Deserialize<OriginSaleEvent>(JsonDefaults.Options)!;
                OriginEvents[originEvent.EventId] = new(entry.Sequence, originEvent);
                break;
            case HubEventTypes.OriginEventAcknowledged:
                AcknowledgedOriginEvents.Add(entry.Payload.GetString()
                    ?? throw new InvalidDataException("Origin event acknowledgement has no event ID."));
                break;
            default:
                throw new InvalidDataException($"Unknown hub event type '{entry.EventType}'.");
        }
    }
}

public sealed record PeerLease(
    string ServerId,
    string SptVersion,
    string CompatibilityHash,
    DateTimeOffset LeaseExpiresAt);

public sealed record StoredResult(string Key, string Operation, JsonElement Result);
public sealed record StoredOriginEvent(long Sequence, OriginSaleEvent Event);

public static class HubEventTypes
{
    public const string PeerRegistered = "peer.registered";
    public const string OfferPublished = "offer.published";
    public const string OfferChanged = "offer.changed";
    public const string ProjectionChanged = "offer.projection-changed";
    public const string ReservationChanged = "reservation.changed";
    public const string IdempotencyRecorded = "idempotency.recorded";
    public const string OriginEventAdded = "origin.event-added";
    public const string OriginEventAcknowledged = "origin.event-acknowledged";
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false
    };
}
