using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossRagfair.Contracts;
using CrossRagfair.Core;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace CrossRagfair.Spt;

[Injectable(InjectionType.Singleton, null, OnLoadOrder.RagfairCallbacks + 1)]
public sealed class CrossRagfairService(
    ISptLogger<CrossRagfairService> logger,
    RagfairOfferService offerService,
    EventOutputHolder eventOutputHolder,
    HttpResponseUtil httpResponseUtil,
    SaveServer saveServer,
    RagfairOfferHelper ragfairOfferHelper) : IOnLoad, IOnUpdate
{
    private const string HarmonyId = "com.mochix2milk.crossragfair";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, CurrencyCode> CurrencyByTemplate = new(StringComparer.Ordinal)
    {
        ["5449016a4bdc2d6f028b456f"] = CurrencyCode.RUB,
        ["5696686a4bdc2da3298b456a"] = CurrencyCode.USD,
        ["569668774bdc2da2298b4568"] = CurrencyCode.EUR
    };
    private static readonly Dictionary<CurrencyCode, string> TemplateByCurrency = CurrencyByTemplate
        .ToDictionary(x => x.Value, x => x.Key);

    private readonly ConcurrentQueue<SharedOffer> _publishQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queuedLocalOffers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteOfferMetadata> _remoteOffers = new(StringComparer.Ordinal);
    private CrossRagfairConfig? _config;
    private HubClient? _hub;
    private long _cursor;
    private long _nextRegistrationUnix;
    private int _updating;
    private NodeStateStore? _nodeStore;
    private PurchaseCoordinator? _purchaseCoordinator;
    private OriginCoordinator? _originCoordinator;
    private readonly CancellationTokenSource _backgroundStop = new();

    internal static CrossRagfairService? Instance { get; private set; }

    public Task OnLoad()
    {
        _config = CrossRagfairConfig.Load();
        if (!_config.Enabled)
        {
            logger.Info("CrossRagfair is disabled by config.json.", null!);
            return Task.CompletedTask;
        }
        _hub = new HubClient(_config);
        var assemblyDirectory = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot locate CrossRagfair assembly directory.");
        _nodeStore = new NodeStateStore(_config.ResolveNodeDataDirectory(assemblyDirectory));
        _purchaseCoordinator = new(_config, _hub, _nodeStore, saveServer, eventOutputHolder,
            httpResponseUtil, logger, IsRemoteOffer);
        _originCoordinator = new(_config, _hub, _nodeStore, offerService, ragfairOfferHelper, saveServer, logger);
        Instance = this;
        RegisterPatches();
        _ = Task.Run(() => _originCoordinator.RunCommandLoopAsync(_backgroundStop.Token));
        logger.Success($"CrossRagfair 0.1.0 loaded for SPT 4.0.13; serverId={_config.ServerId}; readOnly={_config.ReadOnly}.", null!);
        return Task.CompletedTask;
    }

    public async Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (_hub is null || _config is null || Interlocked.Exchange(ref _updating, 1) != 0) return true;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now >= _nextRegistrationUnix)
            {
                var registration = await _hub.RegisterAsync().ConfigureAwait(false);
                if (registration?.Success != true) throw new InvalidOperationException(registration?.Message ?? "Peer registration failed.");
                _nextRegistrationUnix = now + Math.Max(2, _config.OriginLeaseSeconds / 2);
                await _originCoordinator!.SynchronizeOriginOffersAsync().ConfigureAwait(false);
            }

            if (!_config.ReadOnly)
            {
                while (_publishQueue.TryPeek(out var offer))
                {
                    var result = await _hub.PublishAsync(offer).ConfigureAwait(false);
                    if (result?.Success != true) throw new InvalidOperationException(result?.Message ?? "Offer publish failed.");
                    _publishQueue.TryDequeue(out _);
                    _originCoordinator!.RegisterPublishedOffer(offer);
                }
            }

            var page = await _hub.GetProjectionsAsync(_cursor).ConfigureAwait(false);
            if (page is not null)
            {
                foreach (var change in page.Changes) ApplyProjection(change.Offer);
                _cursor = page.NextCursor;
            }
            _purchaseCoordinator!.RecoverAll();
            await _originCoordinator!.ProcessOriginEventsAsync().ConfigureAwait(false);
            _originCoordinator.ReconcileExtendedOffers();
        }
        catch (Exception ex)
        {
            logger.Warning($"CrossRagfair synchronization failed closed: {ex.Message}", ex);
        }
        finally { Volatile.Write(ref _updating, 0); }
        return true;
    }

    internal void CapturePlayerOffers(MongoId sessionId)
    {
        if (_config is null || _config.ReadOnly) return;
        try
        {
            foreach (var offer in offerService.GetOffers().Where(x => x.IsPlayerOffer() && x.User is not null && x.User.Id == sessionId))
            {
                var localId = offer.Id.ToString();
                if (!_queuedLocalOffers.TryAdd(localId, 0)) continue;
                if (TryConvertLocalOffer(offer, sessionId, out var shared, out var error)) _publishQueue.Enqueue(shared!);
                else
                {
                    _queuedLocalOffers.TryRemove(localId, out _);
                    logger.Warning($"Offer {localId} was not shared: {error}", null!);
                }
            }
        }
        catch (Exception ex) { logger.Error("Failed to capture local flea offer.", ex); }
    }

    internal bool IsRemoteOffer(string offerId) => _remoteOffers.ContainsKey(offerId);

    internal bool BeginRemotePurchase(PmcData pmcData, ProcessRagfairTradeRequestData request, MongoId sessionId,
        ref ItemEventRouterResponse result, out PurchasePatchState? state)
    {
        state = null;
        try { return _purchaseCoordinator?.Begin(pmcData, request, sessionId, ref result, out state) ?? true; }
        catch (Exception ex)
        {
            logger.Error("CrossRagfair purchase preflight failed closed.", ex);
            result = httpResponseUtil.AppendErrorToOutput(eventOutputHolder.GetOutput(sessionId),
                "Cross-server purchase preflight failed.", BackendErrorCodes.RagfairUnavailable);
            return false;
        }
    }

    internal void CompleteRemotePurchase(PmcData pmcData, MongoId sessionId, ItemEventRouterResponse result,
        PurchasePatchState? state) => _purchaseCoordinator?.Complete(pmcData, sessionId, result, state);

    internal void FailRemotePurchase(PurchasePatchState? state, Exception exception) =>
        _purchaseCoordinator?.Fail(state, exception);

    internal bool BeginSharedOfferCancellation(MongoId offerId, MongoId sessionId,
        ref ItemEventRouterResponse result)
    {
        try
        {
            var cancelled = _originCoordinator?.Cancel(offerId.ToString(), sessionId.ToString());
            if (cancelled is null || cancelled.Success) return true;
            result = httpResponseUtil.AppendErrorToOutput(eventOutputHolder.GetOutput(sessionId),
                cancelled.Message ?? "Shared offer cancellation was rejected.", BackendErrorCodes.RagfairUnavailable);
            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"Shared offer {offerId} cancellation failed closed.", ex);
            result = httpResponseUtil.AppendErrorToOutput(eventOutputHolder.GetOutput(sessionId),
                "Shared offer cancellation could not reach Hub.", BackendErrorCodes.RagfairUnavailable);
            return false;
        }
    }

    internal void ReconcileSharedOfferExtension(ExtendOfferRequestData request, ItemEventRouterResponse result)
    {
        if (result.Warnings is { Count: > 0 } || string.IsNullOrWhiteSpace(request.OfferId)) return;
        try { _originCoordinator?.ReconcileExtendedOffer(request.OfferId); }
        catch (Exception ex) { logger.Warning($"Shared offer {request.OfferId} extension will be retried.", ex); }
    }

    internal bool ShouldDeferSharedOfferExpiry(MongoId offerId) =>
        _originCoordinator?.ShouldDeferStaleOffer(offerId.ToString()) == true;

    private bool TryConvertLocalOffer(RagfairOffer offer, MongoId sessionId, out SharedOffer? shared, out string? error)
    {
        shared = null;
        error = null;
        var requirements = offer.Requirements?.ToArray() ?? [];
        if (requirements.Length != 1 || offer.Items is not { Count: > 0 } || offer.User is null)
        {
            error = "only one RUB/USD/EUR currency requirement is supported";
            return false;
        }
        var requirementCount = requirements[0].Count;
        if (!CurrencyByTemplate.TryGetValue(requirements[0].TemplateId.ToString(), out var currency) ||
            requirementCount is null or <= 0)
        {
            error = "only one RUB/USD/EUR currency requirement is supported";
            return false;
        }
        var price = checked((long)Math.Round(requirementCount.Value, MidpointRounding.AwayFromZero));
        var globalOfferId = GlobalId(_config!.ServerId, offer.Id.ToString());
        var idMap = offer.Items.ToDictionary(x => x.Id.ToString(), x => GlobalId(globalOfferId, x.Id.ToString()), StringComparer.Ordinal);
        var items = offer.Items.Select(item => new ItemSnapshot(
            idMap[item.Id.ToString()], item.Template.ToString(),
            item.ParentId is not null && idMap.TryGetValue(item.ParentId, out var parent) ? parent : null,
            item.SlotId,
            item.Upd is null ? null : JsonSerializer.SerializeToElement(item.Upd, Json))).ToArray();
        shared = new SharedOffer(globalOfferId, _config.ServerId, offer.Id.ToString(), sessionId.ToString(),
            offer.User.Nickname ?? "Remote player", items, currency, price, offer.Quantity, offer.Quantity,
            offer.SellInOnePiece == true, offer.StartTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            offer.EndTime ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), SharedOfferStatus.Active, 0);
        return true;
    }

    private void ApplyProjection(SharedOffer shared)
    {
        if (_config is null || shared.OriginServerId == _config.ServerId) return;
        if (shared.Status != SharedOfferStatus.Active || shared.RemainingQuantity <= 0)
        {
            if (_remoteOffers.TryRemove(shared.GlobalOfferId, out _) && offerService.DoesOfferExist(shared.GlobalOfferId))
                offerService.RemoveOfferById(shared.GlobalOfferId);
            return;
        }

        if (offerService.DoesOfferExist(shared.GlobalOfferId)) offerService.RemoveOfferById(shared.GlobalOfferId);
        var items = shared.Items.Select(item => new Item
        {
            Id = item.Id,
            Template = item.TemplateId,
            ParentId = item.ParentId,
            SlotId = item.SlotId,
            Upd = item.Upd is null ? null! : item.Upd.Value.Deserialize<Upd>(Json)!
        }).ToList();
        var projection = new RagfairOffer
        {
            Id = shared.GlobalOfferId,
            Root = items[0].Id,
            Items = items,
            Requirements = [new OfferRequirement { TemplateId = TemplateByCurrency[shared.Currency], Count = shared.UnitPrice }],
            User = new RagfairOfferUser
            {
                Id = GlobalId(shared.OriginServerId, shared.OriginProfileId),
                Nickname = shared.SellerNickname,
                MemberType = MemberCategory.Default,
                Rating = 0,
                IsRatingGrowing = true
            },
            Quantity = shared.RemainingQuantity,
            StartTime = shared.StartTimeUnix,
            EndTime = shared.EndTimeUnix,
            SellInOnePiece = shared.SellInOnePiece,
            SummaryCost = shared.UnitPrice,
            RequirementsCost = shared.UnitPrice,
            CreatedBy = OfferCreator.FakePlayer,
            UnlimitedCount = false
        };
        offerService.AddOffer(projection);
        _remoteOffers[shared.GlobalOfferId] = new(shared.OriginServerId, shared.OriginOfferId, shared.Version);
    }

    private void RegisterPatches()
    {
        var method = AccessTools.Method(typeof(RagfairController), nameof(RagfairController.AddPlayerOffer),
            [typeof(PmcData), typeof(AddOfferRequestData), typeof(MongoId)]);
        if (method is null) throw new MissingMethodException("SPT 4.0.13 RagfairController.AddPlayerOffer signature was not found.");
        var postfix = AccessTools.Method(typeof(AddPlayerOfferPatch), nameof(AddPlayerOfferPatch.Postfix));
        var harmony = new Harmony(HarmonyId);
        harmony.Patch(method, postfix: new HarmonyMethod(postfix));
        logger.Debug($"Patched exact target {Format(method)}.", null!);

        var tradeMethod = AccessTools.Method(typeof(TradeController), nameof(TradeController.ConfirmRagfairTrading),
            [typeof(PmcData), typeof(ProcessRagfairTradeRequestData), typeof(MongoId)]);
        if (tradeMethod is null) throw new MissingMethodException("SPT 4.0.13 TradeController.ConfirmRagfairTrading signature was not found.");
        var prefix = AccessTools.Method(typeof(RemotePurchasePatch), nameof(RemotePurchasePatch.Prefix));
        var tradePostfix = AccessTools.Method(typeof(RemotePurchasePatch), nameof(RemotePurchasePatch.Postfix));
        var finalizer = AccessTools.Method(typeof(RemotePurchasePatch), nameof(RemotePurchasePatch.Finalizer));
        harmony.Patch(tradeMethod, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(tradePostfix),
            finalizer: new HarmonyMethod(finalizer));
        logger.Debug($"Patched exact target {Format(tradeMethod)}.", null!);

        var cancelMethod = AccessTools.Method(typeof(RagfairController), nameof(RagfairController.FlagOfferForRemoval),
            [typeof(MongoId), typeof(MongoId)]);
        var cancelPrefix = AccessTools.Method(typeof(SharedOfferLifecyclePatch), nameof(SharedOfferLifecyclePatch.CancelPrefix));
        if (cancelMethod is null) throw new MissingMethodException("SPT 4.0.13 RagfairController.FlagOfferForRemoval signature was not found.");
        harmony.Patch(cancelMethod, prefix: new HarmonyMethod(cancelPrefix));
        logger.Debug($"Patched exact target {Format(cancelMethod)}.", null!);

        var extendMethod = AccessTools.Method(typeof(RagfairController), nameof(RagfairController.ExtendOffer),
            [typeof(ExtendOfferRequestData), typeof(MongoId)]);
        var extendPostfix = AccessTools.Method(typeof(SharedOfferLifecyclePatch), nameof(SharedOfferLifecyclePatch.ExtendPostfix));
        if (extendMethod is null) throw new MissingMethodException("SPT 4.0.13 RagfairController.ExtendOffer signature was not found.");
        harmony.Patch(extendMethod, postfix: new HarmonyMethod(extendPostfix));
        logger.Debug($"Patched exact target {Format(extendMethod)}.", null!);

        var staleMethod = AccessTools.Method(typeof(RagfairOfferService), "ProcessStaleOffer",
            [typeof(MongoId), typeof(bool)]);
        var stalePrefix = AccessTools.Method(typeof(SharedOfferLifecyclePatch), nameof(SharedOfferLifecyclePatch.StalePrefix));
        if (staleMethod is null) throw new MissingMethodException("SPT 4.0.13 RagfairOfferService.ProcessStaleOffer signature was not found.");
        harmony.Patch(staleMethod, prefix: new HarmonyMethod(stalePrefix));
        logger.Debug($"Patched exact target {Format(staleMethod)}.", null!);
    }

    private static string GlobalId(string scope, string localId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\0{localId}")))[..24].ToLowerInvariant();

    private static string Format(MethodBase method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(",", method.GetParameters().Select(x => x.ParameterType.FullName))})";

    private sealed record RemoteOfferMetadata(string OriginServerId, string OriginOfferId, long Version);
}

internal static class AddPlayerOfferPatch
{
    public static void Postfix(MongoId sessionID, ItemEventRouterResponse __result)
    {
        if (__result.Warnings is { Count: > 0 }) return;
        CrossRagfairService.Instance?.CapturePlayerOffers(sessionID);
    }
}

internal static class RemotePurchasePatch
{
    public static bool Prefix(PmcData pmcData, ProcessRagfairTradeRequestData request, MongoId sessionID,
        ref ItemEventRouterResponse __result, out PurchasePatchState? __state)
    {
        __state = null;
        return CrossRagfairService.Instance?.BeginRemotePurchase(pmcData, request, sessionID, ref __result, out __state) ?? true;
    }

    public static void Postfix(PmcData pmcData, MongoId sessionID, ItemEventRouterResponse __result,
        PurchasePatchState? __state) =>
        CrossRagfairService.Instance?.CompleteRemotePurchase(pmcData, sessionID, __result, __state);

    public static Exception? Finalizer(Exception? __exception, PurchasePatchState? __state)
    {
        if (__exception is not null) CrossRagfairService.Instance?.FailRemotePurchase(__state, __exception);
        return __exception;
    }
}

internal static class SharedOfferLifecyclePatch
{
    public static bool CancelPrefix(MongoId offerId, MongoId sessionId, ref ItemEventRouterResponse __result) =>
        CrossRagfairService.Instance?.BeginSharedOfferCancellation(offerId, sessionId, ref __result) ?? true;

    public static void ExtendPostfix(ExtendOfferRequestData extendRequest, ItemEventRouterResponse __result) =>
        CrossRagfairService.Instance?.ReconcileSharedOfferExtension(extendRequest, __result);

    public static bool StalePrefix(MongoId staleOfferId) =>
        CrossRagfairService.Instance?.ShouldDeferSharedOfferExpiry(staleOfferId) != true;
}
