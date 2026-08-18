using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace CrossRagfair.Hub;

public static class HmacAuthentication
{
    public const string ServerHeader = "X-CrossRagfair-Server";
    public const string TimestampHeader = "X-CrossRagfair-Timestamp";
    public const string NonceHeader = "X-CrossRagfair-Nonce";
    public const string SignatureHeader = "X-CrossRagfair-Signature";

    public static string Compute(string secret, string method, string pathAndQuery, string timestamp,
        string nonce, ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        var canonical = $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class HmacMiddleware(RequestDelegate next, HubOptions options)
{
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health")
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        context.Request.Body.Position = 0;

        var serverId = context.Request.Headers[HmacAuthentication.ServerHeader].ToString();
        var timestampText = context.Request.Headers[HmacAuthentication.TimestampHeader].ToString();
        var nonce = context.Request.Headers[HmacAuthentication.NonceHeader].ToString();
        var signature = context.Request.Headers[HmacAuthentication.SignatureHeader].ToString();
        if (!options.PeerSecrets.TryGetValue(serverId, out var secret) ||
            !long.TryParse(timestampText, out var timestamp) || string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signature))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > options.AllowedClockSkewSeconds)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        foreach (var stale in _nonces.Where(x => x.Value < now - options.AllowedClockSkewSeconds).Select(x => x.Key))
            _nonces.TryRemove(stale, out _);
        if (!_nonces.TryAdd($"{serverId}:{nonce}", timestamp))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var expected = HmacAuthentication.Compute(secret, context.Request.Method,
            context.Request.PathBase + context.Request.Path + context.Request.QueryString,
            timestampText, nonce, buffer.ToArray());
        if (!TryFixedTimeHexEquals(expected, signature))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[HmacAuthentication.ServerHeader] = serverId;
        await next(context);
    }

    private static bool TryFixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException) { return false; }
    }
}
