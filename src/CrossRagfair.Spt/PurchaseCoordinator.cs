using System.Security.Cryptography;
using System.Text;
using CrossRagfair.Contracts;
using CrossRagfair.Core;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace CrossRagfair.Spt;

internal sealed record PurchasePatchState(PurchaseSaga Saga, int InitialWarningCount);

internal sealed class PurchaseCoordinator(
    CrossRagfairConfig config,
    HubClient hub,
    NodeStateStore store,
    SaveServer saveServer,
    EventOutputHolder eventOutputHolder,
    HttpResponseUtil httpResponseUtil,
    ISptLogger<CrossRagfairService> logger,
    Func<string, bool> isRemoteOffer)
{
    private const string MarkerPrefix = "CrossRagfair.Purchase.";

    public bool Begin(PmcData pmcData, ProcessRagfairTradeRequestData request, MongoId sessionId,
        ref ItemEventRouterResponse result, out PurchasePatchState? state)
    {
        state = null;
        var offers = request.Offers ?? [];
        var remote = offers.Where(x => x.Id is not null && isRemoteOffer(x.Id)).ToArray();
        if (remote.Length == 0) return true;
        if (!config.EnablePurchases)
            return Reject(sessionId, ref result, "Cross-server purchases are disabled by config.");
        if (remote.Length != 1 || offers.Count != 1 || remote[0].Count is not { } quantity || quantity <= 0)
            return Reject(sessionId, ref result, "Cross-server purchases require exactly one remote offer per request.");

        var offerId = remote[0].Id;
        if (string.IsNullOrWhiteSpace(offerId))
            return Reject(sessionId, ref result, "Cross-server offer identifier is missing.");

        var fingerprint = Fingerprint(sessionId, remote[0]);
        var previous = store.FindRecentSaga(fingerprint, TimeSpan.FromSeconds(60));
        if (previous is not null && previous.Status != PurchaseSagaStatus.Aborted)
        {
            Recover(previous);
            var recovered = store.State.PurchaseSagas[previous.TransactionId];
            if (recovered.Status == PurchaseSagaStatus.Committed)
            {
                result = eventOutputHolder.GetOutput(sessionId);
                return false;
            }
            if (recovered.Status != PurchaseSagaStatus.Aborted)
                return Reject(sessionId, ref result, "A previous matching cross-server purchase is still being recovered.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var reserve = hub.ReserveAsync(offerId,
            new($"reserve:{config.ServerId}:{transactionId}", transactionId, config.ServerId,
                sessionId.ToString(), quantity, 15)).GetAwaiter().GetResult();
        if (reserve?.Success != true)
            return Reject(sessionId, ref result, reserve?.Message ?? "Hub rejected the remote offer reservation.");

        var saga = new PurchaseSaga(transactionId, fingerprint, sessionId.ToString(), offerId, quantity,
            PurchaseSagaStatus.Reserved, now, now);
        store.UpsertPurchaseSaga(saga);
        var applying = hub.MarkBuyerApplyingAsync(transactionId,
            new($"applying:{config.ServerId}:{transactionId}", config.ServerId)).GetAwaiter().GetResult();
        if (applying?.Success != true)
        {
            Abort(saga, applying?.Message ?? "Hub rejected buyer apply transition.");
            return Reject(sessionId, ref result, applying?.Message ?? "Hub rejected buyer apply transition.");
        }
        state = new(saga, result.Warnings?.Count ?? 0);
        return true;
    }

    public void Complete(PmcData pmcData, MongoId sessionId, ItemEventRouterResponse result, PurchasePatchState? state)
    {
        if (state is null) return;
        var saga = state.Saga;
        if ((result.Warnings?.Count ?? 0) > state.InitialWarningCount)
        {
            Abort(saga, "Native buyer operation returned a warning.");
            return;
        }

        try
        {
            saga = saga with { Status = PurchaseSagaStatus.BuyerApplied, UpdatedAt = DateTimeOffset.UtcNow };
            store.UpsertPurchaseSaga(saga);
            pmcData.ExtensionData ??= new(StringComparer.Ordinal);
            pmcData.ExtensionData[MarkerPrefix + saga.TransactionId] = "saved";
            saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();

            saga = saga with { Status = PurchaseSagaStatus.BuyerSaved, UpdatedAt = DateTimeOffset.UtcNow };
            store.UpsertPurchaseSaga(saga);
            var saved = hub.MarkBuyerSavedAsync(saga.TransactionId,
                new($"saved:{config.ServerId}:{saga.TransactionId}", config.ServerId, saga.BuyerProfileId))
                .GetAwaiter().GetResult();
            if (saved?.Success != true) throw new InvalidOperationException(saved?.Message ?? "Hub did not accept buyer save.");
            Commit(saga);
        }
        catch (Exception ex)
        {
            var failed = saga with { LastError = ex.Message, UpdatedAt = DateTimeOffset.UtcNow };
            store.UpsertPurchaseSaga(failed);
            httpResponseUtil.AppendErrorToOutput(result,
                "Cross-server purchase was saved locally and is pending durable Hub recovery.",
                BackendErrorCodes.RagfairUnavailable);
            logger.Error($"CrossRagfair transaction {saga.TransactionId} requires recovery.", ex);
        }
    }

    public void RecoverAll()
    {
        foreach (var saga in store.State.PurchaseSagas.Values
                     .Where(x => x.Status is not (PurchaseSagaStatus.Committed or PurchaseSagaStatus.Aborted)).ToArray())
        {
            try { Recover(saga); }
            catch (Exception ex) { logger.Warning($"Purchase recovery deferred for {saga.TransactionId}: {ex.Message}", ex); }
        }
    }

    public void Fail(PurchasePatchState? state, Exception exception)
    {
        if (state is null) return;
        try { Abort(state.Saga, $"Native buyer operation threw {exception.GetType().Name}: {exception.Message}"); }
        catch (Exception abortException)
        {
            logger.Error($"Failed to abort transaction {state.Saga.TransactionId} after native exception.", abortException);
        }
    }

    private void Recover(PurchaseSaga saga)
    {
        var reservation = hub.GetTransactionAsync(saga.TransactionId).GetAwaiter().GetResult();
        if (reservation is null || reservation.Status is ReservationStatus.Aborted or ReservationStatus.Expired)
        {
            store.UpsertPurchaseSaga(saga with { Status = PurchaseSagaStatus.Aborted, UpdatedAt = DateTimeOffset.UtcNow });
            return;
        }
        if (reservation.Status == ReservationStatus.Committed) { MarkCommitted(saga); return; }

        var markerExists = HasProfileMarker(saga.BuyerProfileId, saga.TransactionId);
        if (!markerExists)
        {
            if (reservation.Status is ReservationStatus.Reserved or ReservationStatus.BuyerApplying)
            {
                Abort(saga, "Recovered buyer profile does not contain the durable transaction marker.");
                return;
            }
            throw new InvalidDataException("Hub reports buyer saved but the profile marker is missing.");
        }

        if (reservation.Status == ReservationStatus.BuyerApplying)
        {
            var saved = hub.MarkBuyerSavedAsync(saga.TransactionId,
                new($"saved:{config.ServerId}:{saga.TransactionId}", config.ServerId, saga.BuyerProfileId))
                .GetAwaiter().GetResult();
            if (saved?.Success != true) throw new InvalidOperationException(saved?.Message ?? "Buyer save recovery failed.");
        }
        Commit(saga);
    }

    private void Commit(PurchaseSaga saga)
    {
        var committed = hub.CommitAsync(saga.TransactionId,
            new($"commit:{config.ServerId}:{saga.TransactionId}", config.ServerId)).GetAwaiter().GetResult();
        if (committed?.Success != true) throw new InvalidOperationException(committed?.Message ?? "Hub commit failed.");
        MarkCommitted(saga);
    }

    private void MarkCommitted(PurchaseSaga saga) => store.UpsertPurchaseSaga(saga with
    {
        Status = PurchaseSagaStatus.Committed,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastError = null
    });

    private void Abort(PurchaseSaga saga, string reason)
    {
        var aborted = hub.AbortAsync(saga.TransactionId,
            new($"abort:{config.ServerId}:{saga.TransactionId}", config.ServerId)).GetAwaiter().GetResult();
        var status = aborted?.Success == true ? PurchaseSagaStatus.Aborted : saga.Status;
        store.UpsertPurchaseSaga(saga with
        {
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastError = reason
        });
    }

    private bool HasProfileMarker(string profileId, string transactionId)
    {
        var pmcData = saveServer.GetProfile(profileId)?.CharacterData?.PmcData;
        return pmcData?.ExtensionData?.ContainsKey(MarkerPrefix + transactionId) == true;
    }

    private bool Reject(MongoId sessionId, ref ItemEventRouterResponse result, string message)
    {
        result = httpResponseUtil.AppendErrorToOutput(eventOutputHolder.GetOutput(sessionId), message,
            BackendErrorCodes.RagfairUnavailable);
        return false;
    }

    private static string Fingerprint(MongoId sessionId, OfferRequest offer)
    {
        var payments = offer.Items?.OrderBy(x => x.Id.ToString(), StringComparer.Ordinal)
            .Select(x => $"{x.Id}:{x.Count}") ?? [];
        var canonical = $"{sessionId}|{offer.Id}|{offer.Count}|{string.Join(";", payments)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
