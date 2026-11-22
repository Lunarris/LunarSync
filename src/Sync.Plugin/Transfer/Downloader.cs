using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Sync.Client.Core;
using Sync.Protocol;

namespace Sync.Plugin.Transfer;

public sealed class Downloader
{
    private const int Parallel = 3;

    private readonly FileCache cache;
    private readonly Func<SyncConnection?> connection;
    private readonly ConcurrentDictionary<string, Task<bool>> inflight = new(StringComparer.Ordinal);
    private readonly object queueGate = new();
    private readonly PriorityQueue<TaskCompletionSource, (int Priority, long Order)> waiting = new();
    private int free = Parallel;
    private long order;
    private long bytesReceived;
    private int active;

    private readonly CancellationToken lifetime;

    public Downloader(FileCache cache, Func<SyncConnection?> connection, CancellationToken lifetime)
    {
        this.cache = cache;
        this.connection = connection;
        this.lifetime = lifetime;
    }

    public long BytesReceived => Interlocked.Read(ref bytesReceived);
    public int Active => Volatile.Read(ref active);
    public int Queued => inflight.Count;
    public string? LastError { get; private set; }

    public Task<bool> EnsureAsync(string hash, string extension, SyncTier tier)
    {
        if (cache.Has(hash))
            return Task.FromResult(true);
        return inflight.GetOrAdd(hash, h => RunAsync(h, extension, (int)tier, lifetime));
    }

    private async Task AcquireAsync(int priority, CancellationToken token)
    {
        TaskCompletionSource ticket;
        lock (queueGate)
        {
            if (free > 0 && waiting.Count == 0)
            {
                free--;
                return;
            }
            ticket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Enqueue(ticket, (priority, order++));
        }
        await using (token.Register(() => ticket.TrySetCanceled(token)))
            await ticket.Task;
    }

    private void ReleaseSlot()
    {
        lock (queueGate)
        {
            while (waiting.TryDequeue(out var next, out _))
            {
                if (next.TrySetResult())
                    return;
            }
            free++;
        }
    }

    private async Task<bool> RunAsync(string hash, string extension, int priority, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            await AcquireAsync(priority, token);
            Interlocked.Increment(ref active);
            try
            {
                if (cache.Has(hash))
                    return true;
                var target = cache.TargetPath(hash, extension);
                if (File.Exists(target))
                {
                    cache.Added(hash, target, new FileInfo(target).Length);
                    return true;
                }
                var conn = connection();
                if (conn is null)
                {
                    LastError = "Not connected.";
                    return false;
                }
                var (size, received) = await conn.DownloadFileAsync(hash, target, token);
                Interlocked.Add(ref bytesReceived, received);
                cache.Added(hash, target, size);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref active);
                ReleaseSlot();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HubException ex)
        {
            LastError = SyncConnection.IsFileError(ex, FileError.NotAvailable) ? "A pair is still uploading the file. Lunar Sync will try to download it again."
                : SyncConnection.IsFileError(ex, RateLimitError.RateLimited) ? "Download limit reached. Waiting."
                : ex.Message;
            return false;
        }
        catch (InvalidDataException)
        {
            LastError = "A file arrived corrupted. It was removed and will be downloaded again.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            Plugin.Log.Error(ex, "File download failed");
            return false;
        }
        finally
        {
            inflight.TryRemove(hash, out _);
        }
    }
}
