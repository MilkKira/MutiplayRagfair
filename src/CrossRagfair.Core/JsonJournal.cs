using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrossRagfair.Core;

public sealed record HubEventEnvelope(
    long Sequence,
    string EventId,
    string? TransactionId,
    string EventType,
    DateTimeOffset Timestamp,
    JsonElement Payload,
    string PreviousHash,
    string Hash);

public sealed record HubSnapshot(long LastSequence, string LastHash, HubState State);

public sealed class JsonJournal : IDisposable
{
    public const string GenesisHash = "GENESIS";
    private readonly string _eventPath;
    private readonly string _snapshotPath;
    private readonly FileStream _lockHandle;
    private readonly FileStream _eventStream;
    private long _lastSequence;
    private string _lastHash = GenesisHash;

    public JsonJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        _eventPath = Path.Combine(directory, "hub.events.jsonl");
        _snapshotPath = Path.Combine(directory, "hub.snapshot.json");
        _lockHandle = new FileStream(Path.Combine(directory, "hub.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        _eventStream = new FileStream(_eventPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.WriteThrough);
        _eventStream.Position = _eventStream.Length;
    }

    public (HubState State, long LastSequence, string LastHash) Recover()
    {
        HubState state = new();
        long snapshotSequence = 0;
        var snapshotHash = GenesisHash;
        if (File.Exists(_snapshotPath))
        {
            var snapshot = JsonSerializer.Deserialize<HubSnapshot>(File.ReadAllText(_snapshotPath), JsonDefaults.Options)
                ?? throw new InvalidDataException("Hub snapshot is empty.");
            state = snapshot.State;
            snapshotSequence = snapshot.LastSequence;
            snapshotHash = snapshot.LastHash;
        }

        byte[] bytes;
        if (File.Exists(_eventPath))
        {
            using var readStream = new FileStream(_eventPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var memory = new MemoryStream();
            readStream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        else bytes = [];
        var completeLength = bytes.Length;
        if (completeLength > 0 && bytes[^1] != (byte)'\n')
        {
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            completeLength = lastNewline + 1;
        }

        if (completeLength != bytes.Length)
        {
            _eventStream.SetLength(completeLength);
            _eventStream.Position = completeLength;
            _eventStream.Flush(true);
        }

        var expectedSequence = 1L;
        var expectedHash = GenesisHash;
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, completeLength));
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<HubEventEnvelope>(line, JsonDefaults.Options)
                ?? throw new InvalidDataException("Empty event entry.");
            if (entry.Sequence != expectedSequence)
                throw new InvalidDataException($"Journal sequence break at {entry.Sequence}; expected {expectedSequence}.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.PreviousHash), Encoding.ASCII.GetBytes(expectedHash)))
                throw new InvalidDataException($"Journal hash-chain break at sequence {entry.Sequence}.");
            var calculated = ComputeHash(entry with { Hash = string.Empty });
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.Hash), Encoding.ASCII.GetBytes(calculated)))
                throw new InvalidDataException($"Journal event hash mismatch at sequence {entry.Sequence}.");
            if (entry.Sequence > snapshotSequence) state.Apply(entry);
            expectedSequence++;
            expectedHash = entry.Hash;
        }

        if (snapshotSequence > expectedSequence - 1 ||
            snapshotSequence == expectedSequence - 1 && snapshotHash != expectedHash)
            throw new InvalidDataException("Snapshot does not match the event journal.");

        _lastSequence = expectedSequence - 1;
        _lastHash = expectedHash;
        return (state, _lastSequence, _lastHash);
    }

    public HubEventEnvelope Append(string eventType, object payload, string? transactionId = null)
    {
        var withoutHash = new HubEventEnvelope(
            ++_lastSequence,
            Guid.NewGuid().ToString("N"),
            transactionId,
            eventType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, JsonDefaults.Options),
            _lastHash,
            string.Empty);
        var entry = withoutHash with { Hash = ComputeHash(withoutHash) };
        var line = JsonSerializer.Serialize(entry, JsonDefaults.Options) + "\n";
        var data = Encoding.UTF8.GetBytes(line);
        _eventStream.Write(data);
        _eventStream.Flush(true);
        _lastHash = entry.Hash;
        return entry;
    }

    public void WriteSnapshot(HubState state)
    {
        var temporaryPath = _snapshotPath + ".tmp";
        var data = JsonSerializer.SerializeToUtf8Bytes(new HubSnapshot(_lastSequence, _lastHash, state), JsonDefaults.Options);
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(temporaryPath, _snapshotPath, true);
    }

    public static string ComputeHash(HubEventEnvelope entry)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(entry with { Hash = string.Empty }, JsonDefaults.Options);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public void Dispose()
    {
        _eventStream.Dispose();
        _lockHandle.Dispose();
    }
}
