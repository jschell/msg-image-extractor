using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MsgImageExtractor.Core;

/// <summary>
/// Channel-based worker pool for processing .msg files.
/// Bounded channel capacity = 1,024, FullMode = DropWrite.
/// </summary>
public sealed class ExtractionQueue : IDisposable
{
    // -------------------------------------------------------------------------
    // Channel and concurrency
    // -------------------------------------------------------------------------
    private const int ChannelCapacity = 1024;

    private readonly Channel<string> _channel;
    private readonly Channel<RetryEntry> _retryChannel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _pauseSemaphore;
    private readonly int _maxConcurrent;
    private readonly List<Task> _workers = new();
    private Task? _retryDrainWorker;

    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------
    private readonly ExtractionEngine _engine;
    private Settings _settings;
    private readonly Logger _logger;

    // -------------------------------------------------------------------------
    // Dedup (in-flight paths)
    // -------------------------------------------------------------------------
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Image hash dedup
    // -------------------------------------------------------------------------
    private readonly ConcurrentDictionary<string, byte> _hashSet = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _dedupSuspended = false;
    private const int DedupSuspensionLimit = 500_000;

    public bool IsDedupSuspended => _dedupSuspended;

    // -------------------------------------------------------------------------
    // Session counters
    // -------------------------------------------------------------------------
    private long _extractedCount;
    private long _failedCount;
    private long _skippedCount;
    private long _droppedCount;
    private long _pendingRetryCount;

    public long ExtractedCount => Interlocked.Read(ref _extractedCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);
    public long SkippedCount => Interlocked.Read(ref _skippedCount);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public int PendingRetryCount => (int)Interlocked.Read(ref _pendingRetryCount);

    // -------------------------------------------------------------------------
    // First-drop notification guard
    // -------------------------------------------------------------------------
    private int _firstDropFired;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    public event Action<ExtractionResult>? ExtractionCompleted;
    public event Action<int>? QueueCountChanged;
    public event Action? DedupSuspended;
    public event Action? FirstDropOccurred;

    // -------------------------------------------------------------------------
    // ctor
    // -------------------------------------------------------------------------
    public ExtractionQueue(
        ExtractionEngine engine,
        Settings settings,
        Logger logger,
        int maxConcurrentExtractions = 2)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
        _maxConcurrent = maxConcurrentExtractions;
        _pauseSemaphore = new SemaphoreSlim(maxConcurrentExtractions, maxConcurrentExtractions);

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _retryChannel = Channel.CreateUnbounded<RetryEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    // -------------------------------------------------------------------------
    // Settings update (e.g. MaxConcurrentExtractions changed at runtime)
    // -------------------------------------------------------------------------
    public void UpdateSettings(Settings settings)
    {
        _settings = settings;
    }

    // -------------------------------------------------------------------------
    // Start / Stop
    // -------------------------------------------------------------------------

    public void Start()
    {
        for (int i = 0; i < _maxConcurrent; i++)
            _workers.Add(Task.Run(WorkerLoopAsync));

        _retryDrainWorker = Task.Run(RetryDrainLoopAsync);
    }

    public async Task StopAsync()
    {
        // Signal shutdown — cancels pending retries immediately
        _shutdownCts.Cancel();

        // Complete the write side; workers will drain and exit
        _channel.Writer.TryComplete();
        _retryChannel.Writer.TryComplete();

        // Wait for all workers to finish their current item
        if (_workers.Count > 0)
            await Task.WhenAll(_workers).ConfigureAwait(false);

        if (_retryDrainWorker is not null)
            await _retryDrainWorker.ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Enqueue
    // -------------------------------------------------------------------------

    public void Enqueue(string path)
    {
        if (!_channel.Writer.TryWrite(path))
        {
            Interlocked.Increment(ref _droppedCount);
            _logger.Warning($"Channel full — dropped: {path}");

            // Fire first-drop notification once per session
            if (Interlocked.Exchange(ref _firstDropFired, 1) == 0)
                FirstDropOccurred?.Invoke();
        }
        else
        {
            QueueCountChanged?.Invoke(_channel.Reader.Count);
        }
    }

    // -------------------------------------------------------------------------
    // Pause / Resume
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drain all in-flight workers. Blocks until all workers have released their permits.
    /// Called by App (orchestrator) as part of Pause: disable watcher first, then BlockWorkers.
    /// </summary>
    public async Task BlockWorkersAsync()
    {
        for (int i = 0; i < _maxConcurrent; i++)
            await _pauseSemaphore.WaitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Release all permits so workers can resume.
    /// </summary>
    public void UnblockWorkers()
    {
        _pauseSemaphore.Release(_maxConcurrent);
    }

    // -------------------------------------------------------------------------
    // Image dedup cache management
    // -------------------------------------------------------------------------

    public void ClearHashSet()
    {
        _hashSet.Clear();
        _dedupSuspended = false;
    }

    // -------------------------------------------------------------------------
    // Worker loop
    // -------------------------------------------------------------------------

    private async Task WorkerLoopAsync()
    {
        var ct = _shutdownCts.Token;
        try
        {
        await foreach (var path in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // In-flight dedup check
            if (!_inFlight.TryAdd(path, 0))
            {
                _logger.Warning($"Duplicate in-flight, discarding: {path}");
                QueueCountChanged?.Invoke(_channel.Reader.Count);
                continue;
            }

            // Pause support
            await _pauseSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                QueueCountChanged?.Invoke(_channel.Reader.Count);

                var hashSet = (_settings.DeduplicateByHash && !_dedupSuspended)
                    ? _hashSet
                    : new ConcurrentDictionary<string, byte>();

                var result = await _engine.ExtractAsync(path, _settings, hashSet, ct)
                    .ConfigureAwait(false);

                // Check dedup suspension threshold
                if (_settings.DeduplicateByHash && !_dedupSuspended &&
                    _hashSet.Count >= DedupSuspensionLimit)
                {
                    _dedupSuspended = true;
                    DedupSuspended?.Invoke();
                    _logger.Warning("Image dedup suspended — cache limit reached.");
                }

                // Update counters
                if (result.AlreadyProcessed)
                {
                    // Counted as neither failed nor skipped — SkipExisting is transparent
                }
                else if (result.IsSuccess)
                {
                    Interlocked.Add(ref _extractedCount, result.ExtractedCount);
                }
                else
                {
                    switch (result.FailureReason)
                    {
                        case ExtractionFailureReason.PasswordProtected:
                        case ExtractionFailureReason.UnsupportedFormat:
                            Interlocked.Increment(ref _skippedCount);
                            break;

                        case ExtractionFailureReason.FileInUse:
                            // Schedule retry; do not increment failed yet
                            ScheduleRetry(path, attempt: 1);
                            break;

                        default:
                            Interlocked.Increment(ref _failedCount);
                            break;
                    }
                }

                ExtractionCompleted?.Invoke(result);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            finally
            {
                _inFlight.TryRemove(path, out _);
                _pauseSemaphore.Release();
                QueueCountChanged?.Invoke(_channel.Reader.Count);
            }
        }
        } // end outer try
        catch (OperationCanceledException) { /* shutdown — channel read cancelled */ }
    }

    // -------------------------------------------------------------------------
    // Retry
    // -------------------------------------------------------------------------

    private sealed record RetryEntry(string Path, int Attempt);

    private void ScheduleRetry(string path, int attempt)
    {
        Interlocked.Increment(ref _pendingRetryCount);
        var cts = _shutdownCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
                _retryChannel.Writer.TryWrite(new RetryEntry(path, attempt));
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref _pendingRetryCount);
            }
        }, cts.Token);
    }

    private async Task RetryDrainLoopAsync()
    {
        var ct = _shutdownCts.Token;
        try
        {
            await foreach (var entry in _retryChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendingRetryCount);

                if (entry.Attempt == 1)
                {
                    if (!_channel.Writer.TryWrite(entry.Path))
                    {
                        Interlocked.Increment(ref _failedCount);
                        _logger.Warning($"Retry drop (channel full): {entry.Path}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    // -------------------------------------------------------------------------
    // Test helper (for capacity overflow test)
    // -------------------------------------------------------------------------

    /// <summary>Writes N items to test drop behaviour. Returns number dropped.</summary>
    internal int TestFillAndCountDrops(int count)
    {
        int dropped = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_channel.Writer.TryWrite($"fake_{i}.msg"))
            {
                dropped++;
                Interlocked.Increment(ref _droppedCount);
            }
        }
        return dropped;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        _pauseSemaphore.Dispose();
    }
}
