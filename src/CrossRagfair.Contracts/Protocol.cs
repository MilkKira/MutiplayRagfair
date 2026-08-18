using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossRagfair.Contracts;

public static class Protocol
{
    public const int Version = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<CurrencyCode>))]
public enum CurrencyCode { RUB, USD, EUR }

[JsonConverter(typeof(JsonStringEnumConverter<SharedOfferStatus>))]
public enum SharedOfferStatus { Active, Cancelled, Expired, SoldOut }

[JsonConverter(typeof(JsonStringEnumConverter<ReservationStatus>))]
public enum ReservationStatus { Reserved, BuyerApplying, BuyerSaved, Committed, Aborted, Expired }

public sealed record ItemSnapshot(
    string Id,
    string TemplateId,
    string? ParentId,
    string? SlotId,
    JsonElement? Upd);

public sealed record SharedOffer(
    string GlobalOfferId,
    string OriginServerId,
    string OriginOfferId,
    string OriginProfileId,
    string SellerNickname,
    IReadOnlyList<ItemSnapshot> Items,
    CurrencyCode Currency,
    long UnitPrice,
    int TotalQuantity,
    int RemainingQuantity,
    bool SellInOnePiece,
    long StartTimeUnix,
    long EndTimeUnix,
    SharedOfferStatus Status,
    long Version,
    JsonElement? NativeOffer = null);

public sealed record PeerRegistration(
    int ProtocolVersion,
    string ServerId,
    string SptVersion,
    string CompatibilityHash,
    int LeaseSeconds);

public sealed record PublishOfferRequest(string IdempotencyKey, SharedOffer Offer);
public sealed record ReserveOfferRequest(
    string IdempotencyKey,
    string TransactionId,
    string BuyerServerId,
    string BuyerProfileId,
    int Quantity,
    int TtlSeconds);
public sealed record TransactionRequest(string IdempotencyKey, string BuyerServerId);
public sealed record BuyerSavedRequest(string IdempotencyKey, string BuyerServerId, string BuyerProfileId);
public sealed record CancelOfferRequest(string IdempotencyKey, string OriginServerId);
public sealed record ExtendSharedOfferRequest(
    string IdempotencyKey,
    string OriginServerId,
    long ExpectedVersion,
    long NewEndTimeUnix);

public sealed record Reservation(
    string TransactionId,
    string OfferId,
    string OriginServerId,
    string BuyerServerId,
    string BuyerProfileId,
    int Quantity,
    long UnitPrice,
    CurrencyCode Currency,
    DateTimeOffset ExpiresAt,
    ReservationStatus Status);

public sealed record ProjectionPage(long Cursor, long NextCursor, IReadOnlyList<ProjectionChange> Changes);
public sealed record ProjectionChange(long Sequence, string Kind, SharedOffer Offer);

public sealed record OriginLockCommand(
    string CommandId,
    string TransactionId,
    string OriginServerId,
    string OriginOfferId,
    int Quantity,
    DateTimeOffset ExpiresAt);

public sealed record OriginLockResult(string CommandId, bool Approved, string? ErrorCode, string? Message);

public sealed record OriginSaleEvent(
    string EventId,
    string TransactionId,
    string OriginServerId,
    string OriginOfferId,
    string OriginProfileId,
    int Quantity,
    DateTimeOffset CreatedAt);

public sealed record OriginEventDelivery(long Sequence, OriginSaleEvent Event);
public sealed record OriginEventPage(long Cursor, long NextCursor, IReadOnlyList<OriginEventDelivery> Events);
public sealed record AckOriginEventRequest(string IdempotencyKey, string OriginServerId);

public sealed record ApiResult<T>(bool Success, string? ErrorCode, string? Message, T? Value)
{
    public static ApiResult<T> Ok(T value) => new(true, null, null, value);
    public static ApiResult<T> Fail(string code, string message) => new(false, code, message, default);
}
