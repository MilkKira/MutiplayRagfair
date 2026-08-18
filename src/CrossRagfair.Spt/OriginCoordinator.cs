using System.Collections.Concurrent;
using CrossRagfair.Contracts;
using CrossRagfair.Core;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace CrossRagfair.Spt;

internal sealed class OriginCoordinator(
    CrossRagfairConfig config,
    HubClient hub,
    NodeStateStore store,
    RagfairOfferService offerService,
    RagfairOfferHelper offerHelper,
    SaveServer saveServer,
    ISptLogger<CrossRagfairService> logger)
{
    private const string MarkerPrefix = "CrossRagfair.OriginEvent.";
    private readonly ConcurrentDictionary<string, LocalOfferLock> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SharedOffer> _sharedOffers = new(StringComparer.Ordinal);

    public async Task RunCommandLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var command = await hub.WaitForOriginCommandAsync(cancellationToken).ConfigureAwait(false);
                if (command is null) continue;
                var result = ValidateAndLock(command);
                await hub.CompleteOriginCommandAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.Warning($"Origin command loop retrying after error: {ex.Message}", ex);
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task SynchronizeOriginOffersAsync(CancellationToken cancellationToken = default)
    {
        var offers = await hub.GetOriginOffersAsync(cancellationToken).ConfigureAwait(false) ?? [];
        _sharedOffers.Clear();
        foreach (var shared in offers)
        {
            _sharedOffers[shared.OriginOfferId] = shared;
            if (!offerService.DoesOfferExist(shared.OriginOfferId)) continue;
            var local = offerService.GetOfferByOfferId(shared.OriginOfferId);
            if (local?.IsPlayerOffer() == true) local.SellResults = [];
        }
    }

    public void RegisterPublishedOffer(SharedOffer shared)
    {
        _sharedOffers[shared.OriginOfferId] = shared;
        if (offerService.DoesOfferExist(shared.OriginOfferId) && offerService.GetOfferByOfferId(shared.OriginOfferId) is { } offer)
            offer.SellResults = [];
    }

    public bool TryGetSharedOffer(string originOfferId, out SharedOffer shared) =>
        _sharedOffers.TryGetValue(originOfferId, out shared!);

    public bool ShouldDeferStaleOffer(string originOfferId) => _sharedOffers.ContainsKey(originOfferId);

    public ApiResult<SharedOffer>? Cancel(string originOfferId, string profileId)
    {
        if (!TryGetSharedOffer(originOfferId, out var shared)) return null;
        if (shared.OriginProfileId != profileId)
            return ApiResult<SharedOffer>.Fail("PROFILE_MISMATCH", "Profile does not own the shared offer.");
        if (!offerService.DoesOfferExist(originOfferId))
            return ApiResult<SharedOffer>.Fail("ORIGIN_OFFER_MISSING", "Origin offer no longer exists.");
        return hub.CancelOfferAsync(shared.GlobalOfferId, shared.Version).GetAwaiter().GetResult();
    }

    public void ReconcileExtendedOffer(string originOfferId)
    {
        if (!TryGetSharedOffer(originOfferId, out var shared) || !offerService.DoesOfferExist(originOfferId)) return;
        var localEnd = offerService.GetOfferByOfferId(originOfferId)?.EndTime;
        if (localEnd is null || localEnd <= shared.EndTimeUnix) return;
        var result = hub.ExtendOfferAsync(shared.GlobalOfferId, shared.Version, localEnd.Value).GetAwaiter().GetResult();
        if (result?.Success == true && result.Value is not null) _sharedOffers[originOfferId] = result.Value;
        else logger.Warning($"Shared offer {originOfferId} extension deferred: {result?.Message ?? "Hub unavailable"}.", null!);
    }

    public void ReconcileExtendedOffers()
    {
        foreach (var originOfferId in _sharedOffers.Keys.ToArray()) ReconcileExtendedOffer(originOfferId);
    }

    public async Task ProcessOriginEventsAsync(CancellationToken cancellationToken = default)
    {
        var page = await hub.GetOriginEventsAsync(store.State.OriginCursor, cancellationToken).ConfigureAwait(false);
        if (page is null) return;
        foreach (var delivery in page.Events)
        {
            try
            {
                ApplyOriginEvent(delivery.Event);
                var ack = await hub.AcknowledgeOriginEventAsync(delivery.Event.EventId, cancellationToken).ConfigureAwait(false);
                if (ack?.Success != true) throw new InvalidOperationException(ack?.Message ?? "Origin event ACK failed.");
                store.SetOriginCursor(delivery.Sequence);
                _locks.TryRemove(delivery.Event.TransactionId, out _);
            }
            catch (Exception ex)
            {
                logger.Error($"Origin settlement {delivery.Event.EventId} deferred: {ex.Message}", ex);
                break;
            }
        }
    }

    private OriginLockResult ValidateAndLock(OriginLockCommand command)
    {
        if (!config.EnablePurchases)
            return new(command.CommandId, false, "PURCHASES_DISABLED", "Origin server has cross-server purchases disabled.");
        if (command.OriginServerId != config.ServerId || command.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(command.CommandId, false, "COMMAND_EXPIRED", "Origin command is invalid or expired.");
        foreach (var expired in _locks.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key))
            _locks.TryRemove(expired, out _);
        if (!_sharedOffers.ContainsKey(command.OriginOfferId) || !offerService.DoesOfferExist(command.OriginOfferId))
            return new(command.CommandId, false, "ORIGIN_OFFER_MISSING", "Origin offer is no longer shared.");
        var offer = offerService.GetOfferByOfferId(command.OriginOfferId);
        if (offer is null || !offer.IsPlayerOffer() || command.Quantity <= 0)
            return new(command.CommandId, false, "ORIGIN_OFFER_INVALID", "Origin offer failed local validation.");
        var locked = _locks.Values.Where(x => x.OriginOfferId == command.OriginOfferId).Sum(x => x.Quantity);
        if (offer.Quantity - locked < command.Quantity || offer.SellInOnePiece == true && offer.Quantity != command.Quantity)
            return new(command.CommandId, false, "ORIGIN_STOCK_CHANGED", "Origin offer quantity changed.");
        _locks[command.TransactionId] = new(command.OriginOfferId, command.Quantity,
            DateTimeOffset.UtcNow.AddSeconds(60));
        return new(command.CommandId, true, null, null);
    }

    private void ApplyOriginEvent(OriginSaleEvent originEvent)
    {
        if (store.State.OriginInbox.TryGetValue(originEvent.EventId, out var existing) &&
            existing.Status == OriginInboxStatus.Applied) return;
        var pmcData = saveServer.GetProfile(originEvent.OriginProfileId)?.CharacterData?.PmcData
            ?? throw new InvalidDataException($"Origin profile {originEvent.OriginProfileId} is missing.");
        pmcData.ExtensionData ??= new(StringComparer.Ordinal);
        if (pmcData.ExtensionData.ContainsKey(MarkerPrefix + originEvent.EventId))
        {
            store.UpsertOriginInbox(ToInbox(originEvent, OriginInboxStatus.Applied));
            return;
        }

        store.UpsertOriginInbox(ToInbox(originEvent, OriginInboxStatus.Applying));
        if (!offerService.DoesOfferExist(originEvent.OriginOfferId))
            throw new InvalidDataException($"Origin offer {originEvent.OriginOfferId} is missing without an applied marker.");
        var offer = offerService.GetOfferByOfferId(originEvent.OriginOfferId);
        if (offer is null || !offer.IsPlayerOffer()) throw new InvalidDataException("Origin offer is not a player offer.");
        var output = offerHelper.CompleteOffer(originEvent.OriginProfileId, offer, originEvent.Quantity);
        if (output.Warnings is { Count: > 0 }) throw new InvalidOperationException("Native origin settlement returned a warning.");
        pmcData.ExtensionData[MarkerPrefix + originEvent.EventId] = "applied";
        saveServer.SaveProfileAsync(originEvent.OriginProfileId).GetAwaiter().GetResult();
        store.UpsertOriginInbox(ToInbox(originEvent, OriginInboxStatus.Applied));
    }

    private static OriginInboxRecord ToInbox(OriginSaleEvent value, OriginInboxStatus status) => new(
        value.EventId, value.TransactionId, value.OriginProfileId, value.OriginOfferId, value.Quantity,
        status, DateTimeOffset.UtcNow);

    private sealed record LocalOfferLock(string OriginOfferId, int Quantity, DateTimeOffset ExpiresAt);
}
