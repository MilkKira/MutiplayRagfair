using System.Text.Json;
using System.Threading.Channels;
using CrossRagfair.Contracts;

namespace CrossRagfair.Core;

public sealed class HubEngine : IAsyncDisposable
{
    private readonly JsonJournal _journal;
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _readLock = new();
    private HubState _state;

    public HubEngine(string dataDirectory)
    {
        _journal = new JsonJournal(dataDirectory);
        try { _state = _journal.Recover().State; }
        catch
        {
            _journal.Dispose();
            throw;
        }
        _worker = Task.Run(ProcessCommandsAsync);
    }

    public Task<ApiResult<PeerLease>> RegisterPeerAsync(PeerRegistration request) => Enqueue(() =>
    {
        ExpireReservations();
        ExpireOffers();
        if (request.ProtocolVersion != Protocol.Version)
            return ApiResult<PeerLease>.Fail("PROTOCOL_MISMATCH", $"Expected protocol {Protocol.Version}.");
        if (string.IsNullOrWhiteSpace(request.ServerId) || request.LeaseSeconds is < 2 or > 120)
            return ApiResult<PeerLease>.Fail("INVALID_PEER", "Server ID or lease duration is invalid.");
        if (string.IsNullOrWhiteSpace(request.SptVersion) || string.IsNullOrWhiteSpace(request.CompatibilityHash))
            return ApiResult<PeerLease>.Fail("INVALID_PEER", "SPT version and compatibility hash are required.");
        var incompatible = _state.Peers.Values.FirstOrDefault(x => x.ServerId != request.ServerId &&
            x.LeaseExpiresAt > DateTimeOffset.UtcNow &&
            (x.SptVersion != request.SptVersion || x.CompatibilityHash != request.CompatibilityHash));
        if (incompatible is not null)
            return ApiResult<PeerLease>.Fail("PEER_INCOMPATIBLE",
                $"Peer {incompatible.ServerId} uses a different SPT version or compatibility hash.");
        var lease = new PeerLease(request.ServerId, request.SptVersion, request.CompatibilityHash,
            DateTimeOffset.UtcNow.AddSeconds(request.LeaseSeconds));
        Persist(HubEventTypes.PeerRegistered, lease);
        return ApiResult<PeerLease>.Ok(lease);
    });

    public Task<ApiResult<SharedOffer>> PublishAsync(PublishOfferRequest request) => Enqueue(() =>
    {
        ExpireReservations();
        ExpireOffers();
        if (TryReplay<SharedOffer>(request.IdempotencyKey, "publish", out var replay)) return replay;
        var offer = request.Offer;
        if (!IsOriginOnline(offer.OriginServerId))
            return Remember(request.IdempotencyKey, "publish", ApiResult<SharedOffer>.Fail("ORIGIN_OFFLINE", "Origin server is offline."));
        if (offer.TotalQuantity <= 0 || offer.RemainingQuantity != offer.TotalQuantity || offer.UnitPrice <= 0 ||
            offer.Items.Count == 0 || offer.EndTimeUnix <= offer.StartTimeUnix)
            return Remember(request.IdempotencyKey, "publish", ApiResult<SharedOffer>.Fail("INVALID_OFFER", "Offer data is invalid."));
        if (_state.Offers.ContainsKey(offer.GlobalOfferId))
            return Remember(request.IdempotencyKey, "publish", ApiResult<SharedOffer>.Fail("OFFER_EXISTS", "Offer ID already exists."));
        offer = offer with { Status = SharedOfferStatus.Active, Version = 1 };
        Persist(HubEventTypes.OfferPublished, offer);
        return Remember(request.IdempotencyKey, "publish", ApiResult<SharedOffer>.Ok(offer));
    });

    public Task<ApiResult<Reservation>> ReserveAsync(string offerId, ReserveOfferRequest request) => Enqueue(() =>
    {
        if (TryReplay<Reservation>(request.IdempotencyKey, "reserve", out var replay)) return replay;
        ExpireReservations();
        if (_state.Reservations.TryGetValue(request.TransactionId, out var existing))
            return Remember(request.IdempotencyKey, "reserve", ApiResult<Reservation>.Ok(existing));
        if (!_state.Offers.TryGetValue(offerId, out var offer) || offer.Status != SharedOfferStatus.Active)
            return Remember(request.IdempotencyKey, "reserve", ApiResult<Reservation>.Fail("OFFER_UNAVAILABLE", "Offer is unavailable."));
        if (!IsOriginOnline(offer.OriginServerId))
            return Remember(request.IdempotencyKey, "reserve", ApiResult<Reservation>.Fail("ORIGIN_OFFLINE", "Origin server is offline."));
        ExpireOffers();
        var reserved = _state.Reservations.Values.Where(x => x.OfferId == offerId && HoldsStock(x.Status))
            .Sum(x => x.Quantity);
        if (request.Quantity <= 0 || offer.RemainingQuantity - reserved < request.Quantity ||
            offer.SellInOnePiece && request.Quantity != offer.RemainingQuantity)
            return Remember(request.IdempotencyKey, "reserve", ApiResult<Reservation>.Fail("INSUFFICIENT_STOCK", "Requested quantity is unavailable."));
        var reservation = new Reservation(request.TransactionId, offerId, offer.OriginServerId,
            request.BuyerServerId, request.BuyerProfileId, request.Quantity, offer.UnitPrice, offer.Currency,
            DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(request.TtlSeconds, 5, 60)), ReservationStatus.Reserved);
        Persist(HubEventTypes.ReservationChanged, reservation, request.TransactionId);
        PersistProjection(offer);
        return Remember(request.IdempotencyKey, "reserve", ApiResult<Reservation>.Ok(reservation));
    });

    public Task<ApiResult<Reservation>> CommitAsync(string transactionId, TransactionRequest request) => Enqueue(() =>
    {
        if (TryReplay<Reservation>(request.IdempotencyKey, "commit", out var replay)) return replay;
        if (!_state.Reservations.TryGetValue(transactionId, out var reservation))
            return Remember(request.IdempotencyKey, "commit", ApiResult<Reservation>.Fail("TX_NOT_FOUND", "Transaction does not exist."));
        if (reservation.BuyerServerId != request.BuyerServerId)
            return Remember(request.IdempotencyKey, "commit", ApiResult<Reservation>.Fail("BUYER_MISMATCH", "Buyer server does not own transaction."));
        if (reservation.Status == ReservationStatus.Committed)
            return Remember(request.IdempotencyKey, "commit", ApiResult<Reservation>.Ok(reservation));
        if (reservation.Status != ReservationStatus.BuyerSaved)
            return Remember(request.IdempotencyKey, "commit", ApiResult<Reservation>.Fail("BUYER_NOT_SAVED", "Buyer profile has not been durably saved."));
        var offer = _state.Offers[reservation.OfferId];
        if (offer.RemainingQuantity < reservation.Quantity)
            throw new InvalidDataException("Authoritative offer quantity is inconsistent with reservation.");
        reservation = reservation with { Status = ReservationStatus.Committed };
        Persist(HubEventTypes.ReservationChanged, reservation, transactionId);
        var remaining = offer.RemainingQuantity - reservation.Quantity;
        offer = offer with
        {
            RemainingQuantity = remaining,
            Status = remaining == 0 ? SharedOfferStatus.SoldOut : SharedOfferStatus.Active,
            Version = offer.Version + 1
        };
        Persist(HubEventTypes.OfferChanged, offer, transactionId);
        var originEvent = new OriginSaleEvent(Guid.NewGuid().ToString("N"), transactionId,
            offer.OriginServerId, offer.OriginOfferId, offer.OriginProfileId, reservation.Quantity,
            DateTimeOffset.UtcNow);
        Persist(HubEventTypes.OriginEventAdded, originEvent, transactionId);
        return Remember(request.IdempotencyKey, "commit", ApiResult<Reservation>.Ok(reservation));
    });

    public Task<ApiResult<SharedOffer>> CancelOfferAsync(string offerId, CancelOfferRequest request) => Enqueue(() =>
    {
        if (TryReplay<SharedOffer>(request.IdempotencyKey, "cancel-offer", out var replay)) return replay;
        ExpireReservations();
        if (!_state.Offers.TryGetValue(offerId, out var offer) || offer.OriginServerId != request.OriginServerId)
            return Remember(request.IdempotencyKey, "cancel-offer",
                ApiResult<SharedOffer>.Fail("OFFER_NOT_OWNED", "Offer does not belong to the origin server."));
        if (offer.Status == SharedOfferStatus.Cancelled)
            return Remember(request.IdempotencyKey, "cancel-offer", ApiResult<SharedOffer>.Ok(offer));
        if (offer.Status != SharedOfferStatus.Active)
            return Remember(request.IdempotencyKey, "cancel-offer",
                ApiResult<SharedOffer>.Fail("OFFER_NOT_ACTIVE", "Only an active offer can be cancelled."));
        if (HasStockHolder(offerId))
            return Remember(request.IdempotencyKey, "cancel-offer",
                ApiResult<SharedOffer>.Fail("OFFER_BUSY", "Offer has an in-flight purchase."));
        offer = offer with { Status = SharedOfferStatus.Cancelled, Version = offer.Version + 1 };
        Persist(HubEventTypes.OfferChanged, offer);
        return Remember(request.IdempotencyKey, "cancel-offer", ApiResult<SharedOffer>.Ok(offer));
    });

    public Task<ApiResult<SharedOffer>> ExtendOfferAsync(string offerId, ExtendSharedOfferRequest request) => Enqueue(() =>
    {
        if (TryReplay<SharedOffer>(request.IdempotencyKey, "extend-offer", out var replay)) return replay;
        ExpireReservations();
        ExpireOffers();
        if (!_state.Offers.TryGetValue(offerId, out var offer) || offer.OriginServerId != request.OriginServerId)
            return Remember(request.IdempotencyKey, "extend-offer",
                ApiResult<SharedOffer>.Fail("OFFER_NOT_OWNED", "Offer does not belong to the origin server."));
        if (offer.Status != SharedOfferStatus.Active)
            return Remember(request.IdempotencyKey, "extend-offer",
                ApiResult<SharedOffer>.Fail("OFFER_NOT_ACTIVE", "Only an active offer can be extended."));
        if (offer.Version != request.ExpectedVersion)
            return Remember(request.IdempotencyKey, "extend-offer",
                ApiResult<SharedOffer>.Fail("VERSION_CONFLICT", "Offer changed before extension."));
        if (HasStockHolder(offerId))
            return Remember(request.IdempotencyKey, "extend-offer",
                ApiResult<SharedOffer>.Fail("OFFER_BUSY", "Offer has an in-flight purchase."));
        if (request.NewEndTimeUnix <= offer.EndTimeUnix || request.NewEndTimeUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return Remember(request.IdempotencyKey, "extend-offer",
                ApiResult<SharedOffer>.Fail("INVALID_END_TIME", "New offer end time must be later."));
        offer = offer with { EndTimeUnix = request.NewEndTimeUnix, Version = offer.Version + 1 };
        Persist(HubEventTypes.OfferChanged, offer);
        return Remember(request.IdempotencyKey, "extend-offer", ApiResult<SharedOffer>.Ok(offer));
    });

    public Task<ApiResult<Reservation>> MarkBuyerSavedAsync(string transactionId, BuyerSavedRequest request) => Enqueue(() =>
    {
        if (TryReplay<Reservation>(request.IdempotencyKey, "buyer-saved", out var replay)) return replay;
        if (!_state.Reservations.TryGetValue(transactionId, out var reservation))
            return Remember(request.IdempotencyKey, "buyer-saved", ApiResult<Reservation>.Fail("TX_NOT_FOUND", "Transaction does not exist."));
        if (reservation.BuyerServerId != request.BuyerServerId || reservation.BuyerProfileId != request.BuyerProfileId)
            return Remember(request.IdempotencyKey, "buyer-saved", ApiResult<Reservation>.Fail("BUYER_MISMATCH", "Buyer does not own transaction."));
        if (reservation.Status is ReservationStatus.BuyerSaved or ReservationStatus.Committed)
            return Remember(request.IdempotencyKey, "buyer-saved", ApiResult<Reservation>.Ok(reservation));
        if (reservation.Status != ReservationStatus.BuyerApplying)
            return Remember(request.IdempotencyKey, "buyer-saved", ApiResult<Reservation>.Fail("TX_NOT_APPLYING", "Buyer transaction is not applying."));
        reservation = reservation with { Status = ReservationStatus.BuyerSaved };
        Persist(HubEventTypes.ReservationChanged, reservation, transactionId);
        return Remember(request.IdempotencyKey, "buyer-saved", ApiResult<Reservation>.Ok(reservation));
    });

    public Task<ApiResult<Reservation>> MarkBuyerApplyingAsync(string transactionId, TransactionRequest request) => Enqueue(() =>
    {
        if (TryReplay<Reservation>(request.IdempotencyKey, "buyer-applying", out var replay)) return replay;
        if (!_state.Reservations.TryGetValue(transactionId, out var reservation))
            return Remember(request.IdempotencyKey, "buyer-applying", ApiResult<Reservation>.Fail("TX_NOT_FOUND", "Transaction does not exist."));
        if (reservation.BuyerServerId != request.BuyerServerId)
            return Remember(request.IdempotencyKey, "buyer-applying", ApiResult<Reservation>.Fail("BUYER_MISMATCH", "Buyer server does not own transaction."));
        if (reservation.Status is ReservationStatus.BuyerApplying or ReservationStatus.BuyerSaved or ReservationStatus.Committed)
            return Remember(request.IdempotencyKey, "buyer-applying", ApiResult<Reservation>.Ok(reservation));
        if (reservation.Status != ReservationStatus.Reserved || reservation.ExpiresAt <= DateTimeOffset.UtcNow)
            return Remember(request.IdempotencyKey, "buyer-applying", ApiResult<Reservation>.Fail("TX_NOT_RESERVED", "Transaction reservation expired."));
        reservation = reservation with { Status = ReservationStatus.BuyerApplying };
        Persist(HubEventTypes.ReservationChanged, reservation, transactionId);
        return Remember(request.IdempotencyKey, "buyer-applying", ApiResult<Reservation>.Ok(reservation));
    });

    public Task<ApiResult<Reservation>> AbortAsync(string transactionId, TransactionRequest request) => Enqueue(() =>
    {
        if (TryReplay<Reservation>(request.IdempotencyKey, "abort", out var replay)) return replay;
        if (!_state.Reservations.TryGetValue(transactionId, out var reservation))
            return Remember(request.IdempotencyKey, "abort", ApiResult<Reservation>.Fail("TX_NOT_FOUND", "Transaction does not exist."));
        if (reservation.BuyerServerId != request.BuyerServerId)
            return Remember(request.IdempotencyKey, "abort", ApiResult<Reservation>.Fail("BUYER_MISMATCH", "Buyer server does not own transaction."));
        if (reservation.Status == ReservationStatus.Committed)
            return Remember(request.IdempotencyKey, "abort", ApiResult<Reservation>.Fail("TX_COMMITTED", "Committed transaction cannot be aborted."));
        if (reservation.Status is ReservationStatus.Reserved or ReservationStatus.BuyerApplying)
        {
            reservation = reservation with { Status = ReservationStatus.Aborted };
            Persist(HubEventTypes.ReservationChanged, reservation, transactionId);
            PersistProjection(_state.Offers[reservation.OfferId]);
        }
        return Remember(request.IdempotencyKey, "abort", ApiResult<Reservation>.Ok(reservation));
    });

    public ProjectionPage GetProjections(long cursor, string requestingServerId)
    {
        lock (_readLock)
        {
            var changes = _state.ProjectionChanges
                .Where(x => x.Sequence > cursor && x.Offer.OriginServerId != requestingServerId)
                .Take(500).ToArray();
            return new(cursor, changes.Length == 0 ? cursor : changes[^1].Sequence, changes);
        }
    }

    public Reservation? GetTransaction(string transactionId)
    {
        lock (_readLock) return _state.Reservations.GetValueOrDefault(transactionId);
    }

    public SharedOffer? GetOffer(string offerId)
    {
        lock (_readLock) return _state.Offers.GetValueOrDefault(offerId);
    }

    public IReadOnlyList<SharedOffer> GetOriginOffers(string originServerId)
    {
        lock (_readLock)
            return _state.Offers.Values.Where(x => x.OriginServerId == originServerId && x.Status == SharedOfferStatus.Active)
                .ToArray();
    }

    public OriginEventPage GetOriginEvents(long cursor, string originServerId)
    {
        lock (_readLock)
        {
            var events = _state.OriginEvents.Values
                .Where(x => x.Sequence > cursor && x.Event.OriginServerId == originServerId &&
                            !_state.AcknowledgedOriginEvents.Contains(x.Event.EventId))
                .OrderBy(x => x.Sequence).Take(100)
                .Select(x => new OriginEventDelivery(x.Sequence, x.Event)).ToArray();
            return new(cursor, events.Length == 0 ? cursor : events[^1].Sequence, events);
        }
    }

    public Task<ApiResult<string>> AcknowledgeOriginEventAsync(string eventId, AckOriginEventRequest request) => Enqueue(() =>
    {
        if (TryReplay<string>(request.IdempotencyKey, "origin-ack", out var replay)) return replay;
        if (!_state.OriginEvents.TryGetValue(eventId, out var stored) || stored.Event.OriginServerId != request.OriginServerId)
            return Remember(request.IdempotencyKey, "origin-ack", ApiResult<string>.Fail("EVENT_NOT_FOUND", "Origin event does not exist."));
        if (!_state.AcknowledgedOriginEvents.Contains(eventId))
            Persist(HubEventTypes.OriginEventAcknowledged, eventId, stored.Event.TransactionId);
        return Remember(request.IdempotencyKey, "origin-ack", ApiResult<string>.Ok(eventId));
    });

    public void WriteSnapshot() { lock (_readLock) _journal.WriteSnapshot(_state); }

    private void ExpireReservations()
    {
        foreach (var reservation in _state.Reservations.Values
                     .Where(x => x.Status == ReservationStatus.Reserved && x.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
        {
            Persist(HubEventTypes.ReservationChanged, reservation with { Status = ReservationStatus.Expired }, reservation.TransactionId);
            PersistProjection(_state.Offers[reservation.OfferId]);
        }
    }

    private void ExpireOffers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var offer in _state.Offers.Values
                     .Where(x => x.Status == SharedOfferStatus.Active && x.EndTimeUnix <= now && !HasStockHolder(x.GlobalOfferId))
                     .ToArray())
            Persist(HubEventTypes.OfferChanged,
                offer with { Status = SharedOfferStatus.Expired, Version = offer.Version + 1 });
    }

    private bool HasStockHolder(string offerId) =>
        _state.Reservations.Values.Any(x => x.OfferId == offerId && HoldsStock(x.Status));

    private static bool HoldsStock(ReservationStatus status) =>
        status is ReservationStatus.Reserved or ReservationStatus.BuyerApplying or ReservationStatus.BuyerSaved;

    private bool IsOriginOnline(string serverId) =>
        _state.Peers.TryGetValue(serverId, out var peer) && peer.LeaseExpiresAt > DateTimeOffset.UtcNow;

    private void Persist(string eventType, object payload, string? transactionId = null)
    {
        var entry = _journal.Append(eventType, payload, transactionId);
        lock (_readLock) _state.Apply(entry);
    }

    private void PersistProjection(SharedOffer offer)
    {
        var reserved = _state.Reservations.Values
            .Where(x => x.OfferId == offer.GlobalOfferId && HoldsStock(x.Status))
            .Sum(x => x.Quantity);
        Persist(HubEventTypes.ProjectionChanged,
            offer with { RemainingQuantity = Math.Max(0, offer.RemainingQuantity - reserved) });
    }

    private ApiResult<T> Remember<T>(string key, string operation, ApiResult<T> result)
    {
        if (!string.IsNullOrWhiteSpace(key))
            Persist(HubEventTypes.IdempotencyRecorded,
                new StoredResult(key, operation, JsonSerializer.SerializeToElement(result, JsonDefaults.Options)));
        return result;
    }

    private bool TryReplay<T>(string key, string operation, out ApiResult<T> result)
    {
        if (!string.IsNullOrWhiteSpace(key) && _state.Idempotency.TryGetValue(key, out var stored))
        {
            result = stored.Operation == operation
                ? stored.Result.Deserialize<ApiResult<T>>(JsonDefaults.Options)!
                : ApiResult<T>.Fail("IDEMPOTENCY_CONFLICT", "Idempotency key was used for another operation.");
            return true;
        }
        result = default!;
        return false;
    }

    private Task<ApiResult<T>> Enqueue<T>(Func<ApiResult<T>> command)
    {
        var completion = new TaskCompletionSource<ApiResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(() =>
            {
                try { completion.SetResult(command()); }
                catch (Exception ex) { completion.SetException(ex); }
                return Task.CompletedTask;
            })) completion.SetException(new ObjectDisposedException(nameof(HubEngine)));
        return completion.Task;
    }

    private async Task ProcessCommandsAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(_stop.Token)) await command();
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        WriteSnapshot();
        _stop.Cancel();
        _stop.Dispose();
        _journal.Dispose();
    }
}
