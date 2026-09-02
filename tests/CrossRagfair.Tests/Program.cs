using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CrossRagfair.Contracts;
using CrossRagfair.Core;
using CrossRagfair.Hub;
using CrossRagfair.Spt;

var tests = new (string Name, Func<Task> Run)[]
{
    ("HMAC is deterministic and body-sensitive", HmacTest),
    ("Hub certificate pin is exact and preserves TLS checks", CertificatePinTest),
    ("Hub options reject weak peer secrets", HubOptionsValidationTest),
    ("origin must be online", OfflineOriginTest),
    ("peer compatibility is a hard gate", PeerCompatibilityTest),
    ("concurrent reservations cannot oversell", ConcurrentReservationTest),
    ("projection quantity excludes active reservations", ProjectionQuantityTest),
    ("buyer applying continues to hold stock", ApplyingHoldsStockTest),
    ("offer lifecycle rejects busy cancellation and supports extension", OfferLifecycleTest),
    ("expired offers are swept on peer heartbeat", OfferExpiryTest),
    ("only owning buyer can abort", AbortOwnershipTest),
    ("commit requires durable buyer save", CommitRequiresBuyerSaveTest),
    ("committed sale creates durable origin event", OriginOutboxTest),
    ("origin command broker requires matching origin", OriginCommandBrokerTest),
    ("idempotent commit survives restart", RecoveryTest),
    ("truncated final JSONL record is ignored", TruncatedTailTest),
    ("middle journal corruption fails closed", CorruptionTest),
    ("node purchase saga survives restart", NodeSagaRecoveryTest)
};

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
return failed == 0 ? 0 : 1;

static Task HmacTest()
{
    var a = HmacAuthentication.Compute("secret", "POST", "/x?a=1", "1", "n", "{}"u8);
    var b = HmacAuthentication.Compute("secret", "POST", "/x?a=1", "1", "n", "{}"u8);
    var c = HmacAuthentication.Compute("secret", "POST", "/x?a=1", "1", "n", "[]"u8);
    Equal(a, b); True(a != c, "Signature must include body hash.");
    return Task.CompletedTask;
}

static Task CertificatePinTest()
{
    var directory = NewDirectory();
    var certificatePath = Path.Combine(directory, "certificate.cer");
    var now = DateTimeOffset.UtcNow;
    using var expectedKey = RSA.Create(2048);
    var expectedRequest = new CertificateRequest("CN=localhost", expectedKey, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    using var expected = expectedRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    File.WriteAllBytes(certificatePath, expected.Export(X509ContentType.Cert));

    using var wrongKey = RSA.Create(2048);
    var wrongRequest = new CertificateRequest("CN=localhost", wrongKey, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    using var wrong = wrongRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    var pin = new HubCertificatePin(certificatePath);

    True(pin.Validate(expected, SslPolicyErrors.RemoteCertificateChainErrors, now),
        "The exact pinned self-signed certificate must be accepted.");
    True(!pin.Validate(wrong, SslPolicyErrors.RemoteCertificateChainErrors, now),
        "A different certificate must be rejected.");
    True(!pin.Validate(expected, SslPolicyErrors.RemoteCertificateNameMismatch, now),
        "A hostname mismatch must be rejected.");
    True(!pin.Validate(expected, SslPolicyErrors.RemoteCertificateChainErrors, now.AddDays(2)),
        "An expired pinned certificate must be rejected.");

    Directory.Delete(directory, true);
    return Task.CompletedTask;
}

static Task HubOptionsValidationTest()
{
    var options = new HubOptions { PeerSecrets = new() { ["server_a"] = "short" } };
    var threw = false;
    try { options.Validate(); } catch (InvalidDataException) { threw = true; }
    True(threw, "Weak Hub peer secret must be rejected.");
    return Task.CompletedTask;
}

static async Task OfflineOriginTest()
{
    await using var temp = new TempEngine();
    var publish = await temp.Engine.PublishAsync(new("p", Offer("o", 1)));
    Equal("ORIGIN_OFFLINE", publish.ErrorCode);
}

static async Task PeerCompatibilityTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    var incompatible = await temp.Engine.RegisterPeerAsync(new(1, "server-b", "4.0.13", "different", 120));
    Equal("PEER_INCOMPATIBLE", incompatible.ErrorCode);
    var compatible = await temp.Engine.RegisterPeerAsync(new(1, "server-b", "4.0.13", "same", 120));
    True(compatible.Success, "Compatible peer registration failed.");
}

static async Task ConcurrentReservationTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    True((await temp.Engine.PublishAsync(new("publish", Offer("o", 1)))).Success, "Publish failed.");
    var attempts = Enumerable.Range(0, 20).Select(i => temp.Engine.ReserveAsync("o",
        new($"reserve-{i}", $"tx-{i}", "server-b", $"buyer-{i}", 1, 15)));
    var results = await Task.WhenAll(attempts);
    Equal(1, results.Count(x => x.Success));
}

static async Task ProjectionQuantityTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 3)));
    await temp.Engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 2, 30));
    var page = temp.Engine.GetProjections(0, "server-b");
    Equal(1, page.Changes[^1].Offer.RemainingQuantity);
}

static async Task ApplyingHoldsStockTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 1)));
    await temp.Engine.ReserveAsync("o", new("reserve-1", "tx-1", "server-b", "buyer", 1, 30));
    await temp.Engine.MarkBuyerApplyingAsync("tx-1", new("applying-1", "server-b"));
    var second = await temp.Engine.ReserveAsync("o", new("reserve-2", "tx-2", "server-b", "buyer-2", 1, 30));
    Equal("INSUFFICIENT_STOCK", second.ErrorCode);
}

static async Task OfferLifecycleTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 2)));
    await temp.Engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 1, 30));
    var busy = await temp.Engine.CancelOfferAsync("o", new("cancel-busy", "server-a"));
    Equal("OFFER_BUSY", busy.ErrorCode);
    await temp.Engine.AbortAsync("tx", new("abort", "server-b"));
    var old = temp.Engine.GetOffer("o")!;
    var extended = await temp.Engine.ExtendOfferAsync("o", new("extend", "server-a", old.Version, old.EndTimeUnix + 3600));
    True(extended.Success, "Offer extension failed.");
    Equal(old.Version + 1, extended.Value!.Version);
    var cancelled = await temp.Engine.CancelOfferAsync("o", new("cancel", "server-a"));
    True(cancelled.Success, "Offer cancellation failed.");
    Equal(SharedOfferStatus.Cancelled, cancelled.Value!.Status);
}

static async Task OfferExpiryTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var expired = Offer("o", 1) with { StartTimeUnix = now - 100, EndTimeUnix = now - 1 };
    await temp.Engine.PublishAsync(new("publish", expired));
    await Register(temp.Engine);
    Equal(SharedOfferStatus.Expired, temp.Engine.GetOffer("o")!.Status);
}

static async Task AbortOwnershipTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 1)));
    await temp.Engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 1, 30));
    var result = await temp.Engine.AbortAsync("tx", new("abort-wrong", "server-c"));
    Equal("BUYER_MISMATCH", result.ErrorCode);
    Equal(ReservationStatus.Reserved, temp.Engine.GetTransaction("tx")!.Status);
}

static async Task RecoveryTest()
{
    var directory = NewDirectory();
    await using (var engine = new HubEngine(directory))
    {
        await Register(engine);
        await engine.PublishAsync(new("publish", Offer("o", 2)));
        await engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 1, 30));
        await engine.MarkBuyerApplyingAsync("tx", new("applying", "server-b"));
        await engine.MarkBuyerSavedAsync("tx", new("saved", "server-b", "buyer"));
        var committed = await engine.CommitAsync("tx", new("commit", "server-b"));
        True(committed.Success, "Commit failed.");
    }
    await using (var engine = new HubEngine(directory))
    {
        var replay = await engine.CommitAsync("tx", new("commit", "server-b"));
        True(replay.Success, "Idempotent replay failed after restart.");
        Equal(ReservationStatus.Committed, replay.Value!.Status);
    }
    Directory.Delete(directory, true);
}

static async Task CommitRequiresBuyerSaveTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 1)));
    await temp.Engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 1, 30));
    var commit = await temp.Engine.CommitAsync("tx", new("commit", "server-b"));
    Equal("BUYER_NOT_SAVED", commit.ErrorCode);
}

static async Task OriginOutboxTest()
{
    await using var temp = new TempEngine();
    await Register(temp.Engine);
    await temp.Engine.PublishAsync(new("publish", Offer("o", 2)));
    await temp.Engine.ReserveAsync("o", new("reserve", "tx", "server-b", "buyer", 1, 30));
    await temp.Engine.MarkBuyerApplyingAsync("tx", new("applying", "server-b"));
    await temp.Engine.MarkBuyerSavedAsync("tx", new("saved", "server-b", "buyer"));
    True((await temp.Engine.CommitAsync("tx", new("commit", "server-b"))).Success, "Commit failed.");
    var page = temp.Engine.GetOriginEvents(0, "server-a");
    Equal(1, page.Events.Count);
    Equal("local-o", page.Events[0].Event.OriginOfferId);
    var eventId = page.Events[0].Event.EventId;
    True((await temp.Engine.AcknowledgeOriginEventAsync(eventId,
        new($"ack:{eventId}", "server-a"))).Success, "Origin ACK failed.");
    Equal(0, temp.Engine.GetOriginEvents(0, "server-a").Events.Count);
}

static async Task OriginCommandBrokerTest()
{
    var broker = new OriginCommandBroker();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var command = new OriginLockCommand("cmd", "tx", "server-a", "offer", 1,
        DateTimeOffset.UtcNow.AddSeconds(2));
    var request = broker.RequestLockAsync(command, timeout.Token);
    var received = await broker.WaitNextAsync("server-a", timeout.Token);
    True(received is not null, "Origin did not receive command.");
    True(!broker.Complete("server-b", new("cmd", true, null, null)), "Wrong origin completed command.");
    True(broker.Complete("server-a", new("cmd", true, null, null)), "Correct origin could not complete command.");
    True((await request).Approved, "Origin approval was not delivered.");
}

static async Task TruncatedTailTest()
{
    var directory = NewDirectory();
    await using (var engine = new HubEngine(directory)) await Register(engine);
    await File.AppendAllTextAsync(Path.Combine(directory, "hub.events.jsonl"), "{\"sequence\":2");
    await using (var recovered = new HubEngine(directory))
        True((await recovered.RegisterPeerAsync(new(1, "server-b", "4.0.13", "same", 10))).Success,
            "Recovery after truncated tail failed.");
    Directory.Delete(directory, true);
}

static async Task CorruptionTest()
{
    var directory = NewDirectory();
    await using (var engine = new HubEngine(directory))
    {
        await Register(engine);
        await engine.PublishAsync(new("publish", Offer("o", 1)));
    }
    var path = Path.Combine(directory, "hub.events.jsonl");
    var lines = await File.ReadAllLinesAsync(path);
    using (var document = JsonDocument.Parse(lines[0]))
    {
        var changed = lines[0].Replace("server-a", "server-x", StringComparison.Ordinal);
        lines[0] = changed;
    }
    await File.WriteAllLinesAsync(path, lines);
    var threw = false;
    try { await using var ignored = new HubEngine(directory); }
    catch (InvalidDataException) { threw = true; }
    True(threw, "Corrupt journal must fail closed.");
    Directory.Delete(directory, true);
}

static Task NodeSagaRecoveryTest()
{
    var directory = NewDirectory();
    var now = DateTimeOffset.UtcNow;
    using (var store = new NodeStateStore(directory))
        store.UpsertPurchaseSaga(new("tx", "fingerprint", "buyer", "offer", 1,
            PurchaseSagaStatus.BuyerSaved, now, now));
    using (var store = new NodeStateStore(directory))
    {
        Equal(PurchaseSagaStatus.BuyerSaved, store.State.PurchaseSagas["tx"].Status);
        Equal("tx", store.FindRecentSaga("fingerprint", TimeSpan.FromMinutes(1))!.TransactionId);
    }
    Directory.Delete(directory, true);
    return Task.CompletedTask;
}

static Task Register(HubEngine engine) => engine.RegisterPeerAsync(new(1, "server-a", "4.0.13", "same", 120));

static SharedOffer Offer(string id, int quantity) => new(id, "server-a", $"local-{id}", "seller",
    "Seller", [new("item", "tpl", null, null, null)], CurrencyCode.RUB, 1000, quantity, quantity,
    false, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
    SharedOfferStatus.Active, 0);

static string NewDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "CrossRagfair.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void True(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

sealed class TempEngine : IAsyncDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "CrossRagfair.Tests", Guid.NewGuid().ToString("N"));
    public HubEngine Engine { get; }
    public TempEngine()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Engine = new HubEngine(Directory);
    }
    public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
}
