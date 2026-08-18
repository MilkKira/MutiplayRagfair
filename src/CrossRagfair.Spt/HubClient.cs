using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrossRagfair.Contracts;

namespace CrossRagfair.Spt;

public sealed class HubClient : IDisposable
{
    private readonly CrossRagfairConfig _config;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public HubClient(CrossRagfairConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new(config.HubUrl), Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<ApiResult<object>?> RegisterAsync(CancellationToken cancellationToken = default) => SendAsync<ApiResult<object>>(
        HttpMethod.Post, "/api/v1/peers/register", new PeerRegistration(Protocol.Version, _config.ServerId,
            _config.SptVersion, _config.CompatibilityHash, _config.OriginLeaseSeconds), cancellationToken);

    public Task<ApiResult<SharedOffer>?> PublishAsync(SharedOffer offer, CancellationToken cancellationToken = default) =>
        SendAsync<ApiResult<SharedOffer>>(HttpMethod.Post, "/api/v1/offers/publish",
            new PublishOfferRequest($"publish:{_config.ServerId}:{offer.OriginOfferId}:{offer.Version}", offer), cancellationToken);

    public Task<ApiResult<SharedOffer>?> CancelOfferAsync(string offerId, long version,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<SharedOffer>>(
        HttpMethod.Post, $"/api/v1/offers/{offerId}/cancel",
        new CancelOfferRequest($"cancel:{_config.ServerId}:{offerId}:{version}", _config.ServerId), cancellationToken);

    public Task<ApiResult<SharedOffer>?> ExtendOfferAsync(string offerId, long expectedVersion, long newEndTimeUnix,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<SharedOffer>>(
        HttpMethod.Post, $"/api/v1/offers/{offerId}/extend",
        new ExtendSharedOfferRequest($"extend:{_config.ServerId}:{offerId}:{expectedVersion}:{newEndTimeUnix}",
            _config.ServerId, expectedVersion, newEndTimeUnix), cancellationToken);

    public Task<ProjectionPage?> GetProjectionsAsync(long cursor, CancellationToken cancellationToken = default) =>
        SendAsync<ProjectionPage>(HttpMethod.Get, $"/api/v1/projections?cursor={cursor}", null, cancellationToken);

    public Task<ApiResult<Reservation>?> ReserveAsync(string offerId, ReserveOfferRequest request,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<Reservation>>(
        HttpMethod.Post, $"/api/v1/offers/{offerId}/reserve", request, cancellationToken);

    public Task<ApiResult<Reservation>?> MarkBuyerSavedAsync(string transactionId, BuyerSavedRequest request,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<Reservation>>(
        HttpMethod.Post, $"/api/v1/transactions/{transactionId}/buyer-saved", request, cancellationToken);

    public Task<ApiResult<Reservation>?> MarkBuyerApplyingAsync(string transactionId, TransactionRequest request,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<Reservation>>(
        HttpMethod.Post, $"/api/v1/transactions/{transactionId}/buyer-applying", request, cancellationToken);

    public Task<ApiResult<Reservation>?> CommitAsync(string transactionId, TransactionRequest request,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<Reservation>>(
        HttpMethod.Post, $"/api/v1/transactions/{transactionId}/commit", request, cancellationToken);

    public Task<ApiResult<Reservation>?> AbortAsync(string transactionId, TransactionRequest request,
        CancellationToken cancellationToken = default) => SendAsync<ApiResult<Reservation>>(
        HttpMethod.Post, $"/api/v1/transactions/{transactionId}/abort", request, cancellationToken);

    public Task<Reservation?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default) =>
        SendAsync<Reservation>(HttpMethod.Get, $"/api/v1/transactions/{transactionId}", null, cancellationToken);

    public Task<OriginLockCommand?> WaitForOriginCommandAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OriginLockCommand>(HttpMethod.Get, "/api/v1/origin/commands/next", null, cancellationToken, true);

    public Task<object?> CompleteOriginCommandAsync(OriginLockResult result, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Post, "/api/v1/origin/commands/result", result, cancellationToken);

    public Task<OriginEventPage?> GetOriginEventsAsync(long cursor, CancellationToken cancellationToken = default) =>
        SendAsync<OriginEventPage>(HttpMethod.Get, $"/api/v1/origin/events?cursor={cursor}", null, cancellationToken);

    public Task<IReadOnlyList<SharedOffer>?> GetOriginOffersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<SharedOffer>>(HttpMethod.Get, "/api/v1/origin/offers", null, cancellationToken);

    public Task<ApiResult<string>?> AcknowledgeOriginEventAsync(string eventId, CancellationToken cancellationToken = default) =>
        SendAsync<ApiResult<string>>(HttpMethod.Post, $"/api/v1/origin/events/{eventId}/ack",
            new AckOriginEventRequest($"ack:{_config.ServerId}:{eventId}", _config.ServerId), cancellationToken);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        bool longPoll = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(longPoll ? TimeSpan.FromSeconds(25) : TimeSpan.FromMilliseconds(_config.RequestTimeoutMilliseconds));
        var bodyBytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, _json);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new ByteArrayContent(bodyBytes) { Headers = { ContentType = new("application/json") } };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var signature = ComputeSignature(_config.SharedSecret, method.Method, path, timestamp, nonce, bodyBytes);
        request.Headers.Add("X-CrossRagfair-Server", _config.ServerId);
        request.Headers.Add("X-CrossRagfair-Timestamp", timestamp);
        request.Headers.Add("X-CrossRagfair-Nonce", nonce);
        request.Headers.Add("X-CrossRagfair-Signature", signature);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_json, timeout.Token).ConfigureAwait(false);
    }

    internal static string ComputeSignature(string secret, string method, string path, string timestamp,
        string nonce, ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}\n{bodyHash}";
        return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose() => _http.Dispose();
}
