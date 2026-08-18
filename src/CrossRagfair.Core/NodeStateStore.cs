using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrossRagfair.Core;

public enum PurchaseSagaStatus { Reserved, BuyerApplied, BuyerSaved, Committed, Aborted }
public enum OriginInboxStatus { Applying, Applied }

public sealed record PurchaseSaga(
    string TransactionId,
    string RequestFingerprint,
    string BuyerProfileId,
    string GlobalOfferId,
    int Quantity,
    PurchaseSagaStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError = null);

public sealed record OriginInboxRecord(
    string EventId,
    string TransactionId,
    string OriginProfileId,
    string OriginOfferId,
    int Quantity,
    OriginInboxStatus Status,
    DateTimeOffset UpdatedAt);

public sealed class NodeState
{
    public Dictionary<string, PurchaseSaga> PurchaseSagas { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, OriginInboxRecord> OriginInbox { get; init; } = new(StringComparer.Ordinal);
    public long OriginCursor { get; set; }

    public void Apply(NodeEventEnvelope entry)
    {
        switch (entry.EventType)
        {
            case NodeEventTypes.PurchaseSagaUpserted:
                var saga = entry.Payload.Deserialize<PurchaseSaga>(JsonDefaults.Options)!;
                PurchaseSagas[saga.TransactionId] = saga;
                break;
            case NodeEventTypes.OriginInboxUpserted:
                var inbox = entry.Payload.Deserialize<OriginInboxRecord>(JsonDefaults.Options)!;
                OriginInbox[inbox.EventId] = inbox;
                break;
            case NodeEventTypes.OriginCursorChanged:
                OriginCursor = entry.Payload.GetInt64();
                break;
            default:
                throw new InvalidDataException($"Unknown node event type '{entry.EventType}'.");
        }
    }
}

public static class NodeEventTypes
{
    public const string PurchaseSagaUpserted = "purchase-saga.upserted";
    public const string OriginInboxUpserted = "origin-inbox.upserted";
    public const string OriginCursorChanged = "origin-cursor.changed";
}

public sealed record NodeEventEnvelope(
    long Sequence,
    string EventId,
    string? TransactionId,
    string EventType,
    DateTimeOffset Timestamp,
    JsonElement Payload,
    string PreviousHash,
    string Hash);

public sealed record NodeSnapshot(long LastSequence, string LastHash, NodeState State);

public sealed class NodeStateStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _eventPath;
    private readonly string _snapshotPath;
    private readonly FileStream _lockHandle;
    private readonly FileStream _eventStream;
    private long _lastSequence;
    private string _lastHash = JsonJournal.GenesisHash;

    public NodeState State { get; private set; } = new();

    public NodeStateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _eventPath = Path.Combine(directory, "node.events.jsonl");
        _snapshotPath = Path.Combine(directory, "node.snapshot.json");
        _lockHandle = new FileStream(Path.Combine(directory, "node.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        FileStream? eventStream = null;
        try
        {
            eventStream = new FileStream(_eventPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.WriteThrough);
            _eventStream = eventStream;
            _eventStream.Position = _eventStream.Length;
            Recover();
        }
        catch
        {
            eventStream?.Dispose();
            _lockHandle.Dispose();
            throw;
        }
    }

    public void UpsertPurchaseSaga(PurchaseSaga saga) => Append(NodeEventTypes.PurchaseSagaUpserted, saga, saga.TransactionId);
    public void UpsertOriginInbox(OriginInboxRecord record) => Append(NodeEventTypes.OriginInboxUpserted, record, record.TransactionId);
    public void SetOriginCursor(long cursor) => Append(NodeEventTypes.OriginCursorChanged, cursor);

    public PurchaseSaga? FindRecentSaga(string fingerprint, TimeSpan window)
    {
        lock (_gate)
            return State.PurchaseSagas.Values.Where(x => x.RequestFingerprint == fingerprint &&
                    x.CreatedAt >= DateTimeOffset.UtcNow.Subtract(window))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    public void WriteSnapshot()
    {
        lock (_gate)
        {
            var temporaryPath = _snapshotPath + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new NodeSnapshot(_lastSequence, _lastHash, State), JsonDefaults.Options);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporaryPath, _snapshotPath, true);
        }
    }

    private void Recover()
    {
        long snapshotSequence = 0;
        var snapshotHash = JsonJournal.GenesisHash;
        if (File.Exists(_snapshotPath))
        {
            var snapshot = JsonSerializer.Deserialize<NodeSnapshot>(File.ReadAllText(_snapshotPath), JsonDefaults.Options)
                ?? throw new InvalidDataException("Node snapshot is empty.");
            State = snapshot.State;
            snapshotSequence = snapshot.LastSequence;
            snapshotHash = snapshot.LastHash;
        }

        byte[] bytes;
        using (var read = new FileStream(_eventPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
        using (var memory = new MemoryStream()) { read.CopyTo(memory); bytes = memory.ToArray(); }
        var completeLength = bytes.Length;
        if (completeLength > 0 && bytes[^1] != (byte)'\n') completeLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        if (completeLength != bytes.Length)
        {
            _eventStream.SetLength(completeLength);
            _eventStream.Position = completeLength;
            _eventStream.Flush(true);
        }

        var expectedSequence = 1L;
        var expectedHash = JsonJournal.GenesisHash;
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, completeLength));
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<NodeEventEnvelope>(line, JsonDefaults.Options)
                ?? throw new InvalidDataException("Empty node event entry.");
            if (entry.Sequence != expectedSequence || entry.PreviousHash != expectedHash || ComputeHash(entry with { Hash = "" }) != entry.Hash)
                throw new InvalidDataException($"Node journal integrity failure at sequence {entry.Sequence}.");
            if (entry.Sequence > snapshotSequence) State.Apply(entry);
            expectedSequence++;
            expectedHash = entry.Hash;
        }
        if (snapshotSequence > expectedSequence - 1 || snapshotSequence == expectedSequence - 1 && snapshotHash != expectedHash)
            throw new InvalidDataException("Node snapshot does not match the event journal.");
        _lastSequence = expectedSequence - 1;
        _lastHash = expectedHash;
    }

    private void Append(string eventType, object payload, string? transactionId = null)
    {
        lock (_gate)
        {
            var incomplete = new NodeEventEnvelope(++_lastSequence, Guid.NewGuid().ToString("N"), transactionId,
                eventType, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload, JsonDefaults.Options),
                _lastHash, "");
            var entry = incomplete with { Hash = ComputeHash(incomplete) };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonDefaults.Options) + "\n");
            _eventStream.Write(bytes);
            _eventStream.Flush(true);
            _lastHash = entry.Hash;
            State.Apply(entry);
        }
    }

    private static string ComputeHash(NodeEventEnvelope entry) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(entry with { Hash = "" }, JsonDefaults.Options)));

    public void Dispose()
    {
        WriteSnapshot();
        _eventStream.Dispose();
        _lockHandle.Dispose();
    }
}
