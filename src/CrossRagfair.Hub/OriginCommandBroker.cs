using System.Collections.Concurrent;
using System.Threading.Channels;
using CrossRagfair.Contracts;

namespace CrossRagfair.Hub;

public sealed class OriginCommandBroker
{
    private readonly ConcurrentDictionary<string, Channel<OriginLockCommand>> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);

    public async Task<OriginLockResult> RequestLockAsync(OriginLockCommand command, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<OriginLockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(command.CommandId, new(command.OriginServerId, completion)))
            return new(command.CommandId, false, "COMMAND_EXISTS", "Origin validation command already exists.");
        try
        {
            await Queue(command.OriginServerId).Writer.WriteAsync(command, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(command.CommandId, false, "ORIGIN_TIMEOUT", "Origin server did not confirm the offer in time.");
        }
        finally { _pending.TryRemove(command.CommandId, out _); }
    }

    public async Task<OriginLockCommand?> WaitNextAsync(string originServerId, CancellationToken cancellationToken)
    {
        try { return await Queue(originServerId).Reader.ReadAsync(cancellationToken); }
        catch (OperationCanceledException) { return null; }
    }

    public bool Complete(string originServerId, OriginLockResult result)
    {
        if (!_pending.TryGetValue(result.CommandId, out var pending) || pending.OriginServerId != originServerId) return false;
        return pending.Completion.TrySetResult(result);
    }

    private Channel<OriginLockCommand> Queue(string originServerId) => _queues.GetOrAdd(originServerId,
        _ => Channel.CreateUnbounded<OriginLockCommand>(new() { SingleReader = false, SingleWriter = false }));

    private sealed record PendingCommand(string OriginServerId, TaskCompletionSource<OriginLockResult> Completion);
}
