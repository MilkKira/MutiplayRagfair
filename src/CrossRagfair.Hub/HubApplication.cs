using CrossRagfair.Contracts;
using CrossRagfair.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrossRagfair.Hub;

public static class HubApplication
{
    public static WebApplication Build(string[] args, HubOptions? suppliedOptions = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var options = suppliedOptions ?? builder.Configuration.GetSection("CrossRagfairHub").Get<HubOptions>() ?? new();
        options.Validate();
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => new HubEngine(Path.GetFullPath(options.DataDirectory)));
        builder.Services.AddSingleton<OriginCommandBroker>();
        var app = builder.Build();
        app.UseMiddleware<HmacMiddleware>();

        app.MapPost("/api/v1/peers/register", async (PeerRegistration request, HubEngine hub, HttpContext context) =>
            request.ServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.RegisterPeerAsync(request))
                : Results.Json(ApiResult<PeerLease>.Fail("SERVER_MISMATCH", "Authenticated server does not match request.")));
        app.MapPost("/api/v1/offers/publish", async (PublishOfferRequest request, HubEngine hub, HttpContext context) =>
            request.Offer.OriginServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.PublishAsync(request))
                : Results.Json(ApiResult<SharedOffer>.Fail("SERVER_MISMATCH", "Authenticated server does not own offer.")));
        app.MapPost("/api/v1/offers/{offerId}/cancel", async (string offerId, CancelOfferRequest request,
            HubEngine hub, HttpContext context) => request.OriginServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.CancelOfferAsync(offerId, request))
                : Results.Json(ApiResult<SharedOffer>.Fail("SERVER_MISMATCH", "Authenticated server does not own offer.")));
        app.MapPost("/api/v1/offers/{offerId}/extend", async (string offerId, ExtendSharedOfferRequest request,
            HubEngine hub, HttpContext context) => request.OriginServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.ExtendOfferAsync(offerId, request))
                : Results.Json(ApiResult<SharedOffer>.Fail("SERVER_MISMATCH", "Authenticated server does not own offer.")));
        app.MapPost("/api/v1/offers/{offerId}/reserve", async (string offerId, ReserveOfferRequest request,
            HubEngine hub, OriginCommandBroker broker, HubOptions hubOptions, HttpContext context) =>
        {
            if (request.BuyerServerId != AuthenticatedServer(context))
                return Results.Json(ApiResult<Reservation>.Fail("SERVER_MISMATCH", "Authenticated server is not the buyer."));
            var offer = hub.GetOffer(offerId);
            if (offer is null) return Results.Json(ApiResult<Reservation>.Fail("OFFER_UNAVAILABLE", "Offer is unavailable."));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(hubOptions.OriginCommandTimeoutMilliseconds);
            var command = new OriginLockCommand(request.TransactionId, request.TransactionId,
                offer.OriginServerId, offer.OriginOfferId, request.Quantity,
                DateTimeOffset.UtcNow.AddMilliseconds(hubOptions.OriginCommandTimeoutMilliseconds));
            var originResult = await broker.RequestLockAsync(command, timeout.Token);
            if (!originResult.Approved)
                return Results.Json(ApiResult<Reservation>.Fail(originResult.ErrorCode ?? "ORIGIN_REJECTED",
                    originResult.Message ?? "Origin server rejected the offer."));
            return Results.Json(await hub.ReserveAsync(offerId, request));
        });
        app.MapPost("/api/v1/transactions/{transactionId}/buyer-saved", async (string transactionId,
            BuyerSavedRequest request, HubEngine hub, HttpContext context) => request.BuyerServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.MarkBuyerSavedAsync(transactionId, request))
                : Results.Json(ApiResult<Reservation>.Fail("SERVER_MISMATCH", "Authenticated server is not the buyer.")));
        app.MapPost("/api/v1/transactions/{transactionId}/buyer-applying", async (string transactionId,
            TransactionRequest request, HubEngine hub, HttpContext context) => request.BuyerServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.MarkBuyerApplyingAsync(transactionId, request))
                : Results.Json(ApiResult<Reservation>.Fail("SERVER_MISMATCH", "Authenticated server is not the buyer.")));
        app.MapPost("/api/v1/transactions/{transactionId}/commit", async (string transactionId, TransactionRequest request,
            HubEngine hub, HttpContext context) => request.BuyerServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.CommitAsync(transactionId, request))
                : Results.Json(ApiResult<Reservation>.Fail("SERVER_MISMATCH", "Authenticated server is not the buyer.")));
        app.MapPost("/api/v1/transactions/{transactionId}/abort", async (string transactionId, TransactionRequest request,
            HubEngine hub, HttpContext context) => request.BuyerServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.AbortAsync(transactionId, request))
                : Results.Json(ApiResult<Reservation>.Fail("SERVER_MISMATCH", "Authenticated server is not the buyer.")));
        app.MapGet("/api/v1/transactions/{transactionId}", (string transactionId, HubEngine hub) =>
            hub.GetTransaction(transactionId) is { } transaction ? Results.Json(transaction) : Results.NotFound());
        app.MapGet("/api/v1/projections", (long? cursor, HttpContext context, HubEngine hub) =>
            Results.Json(hub.GetProjections(cursor ?? 0,
                (string)context.Items[HmacAuthentication.ServerHeader]!)));
        app.MapGet("/api/v1/origin/commands/next", async (HttpContext context, OriginCommandBroker broker) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var serverId = (string)context.Items[HmacAuthentication.ServerHeader]!;
            var command = await broker.WaitNextAsync(serverId, timeout.Token);
            return command is null ? Results.NoContent() : Results.Json(command);
        });
        app.MapPost("/api/v1/origin/commands/result", (OriginLockResult result, HttpContext context,
            OriginCommandBroker broker) => broker.Complete(
                (string)context.Items[HmacAuthentication.ServerHeader]!, result)
                ? Results.Ok() : Results.NotFound());
        app.MapGet("/api/v1/origin/events", (long? cursor, HttpContext context, HubEngine hub) =>
            Results.Json(hub.GetOriginEvents(cursor ?? 0,
                (string)context.Items[HmacAuthentication.ServerHeader]!)));
        app.MapGet("/api/v1/origin/offers", (HttpContext context, HubEngine hub) => Results.Json(
            hub.GetOriginOffers((string)context.Items[HmacAuthentication.ServerHeader]!)));
        app.MapPost("/api/v1/origin/events/{eventId}/ack", async (string eventId, AckOriginEventRequest request,
            HubEngine hub, HttpContext context) => request.OriginServerId == AuthenticatedServer(context)
                ? Results.Json(await hub.AcknowledgeOriginEventAsync(eventId, request))
                : Results.Json(ApiResult<string>.Fail("SERVER_MISMATCH", "Authenticated server is not the origin.")));
        app.MapGet("/health", () => Results.Json(new { status = "ok", protocol = Protocol.Version }));
        return app;
    }

    private static string AuthenticatedServer(HttpContext context) =>
        (string)context.Items[HmacAuthentication.ServerHeader]!;
}
