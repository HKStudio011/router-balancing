using System.Diagnostics;
using System.Text;

namespace router_balancing_test.Engine;

/// <summary>
/// Script byte/EOF/exception theo từng lần đọc. EOF chỉ là một step, nên step byte sau đó
/// mô phỏng nguồn non-standard có thể resume mà MemoryStream không thể hiện được.
/// </summary>
internal sealed class ResumableSseStream : Stream
{
    private readonly Queue<Step> _steps;
    private readonly List<TimeSpan> _readTimes = [];
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private Step? _currentStep;
    private int _currentOffset;
    private bool _delayPending;
    private int _activeReads;

    internal sealed record Step(TimeSpan? Delay, byte[]? Content, Exception? Error);

    internal ResumableSseStream(params Step[] steps)
    {
        _steps = new Queue<Step>(steps);
    }

    internal static Step Content(string value, TimeSpan? delay = null) =>
        new(delay, Encoding.UTF8.GetBytes(value), null);

    internal static Step Eof(TimeSpan? delay = null) => new(delay, null, null);

    internal static Step Failure(Exception exception, TimeSpan? delay = null) =>
        new(delay, null, exception);

    internal int ReadCount { get; private set; }

    internal int MaxConcurrentReads { get; private set; }

    internal IReadOnlyList<TimeSpan> ReadTimes
    {
        get
        {
            lock (_gate) return _readTimes.ToArray();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();

        Step? step;
        var shouldDelay = false;
        lock (_gate)
        {
            _activeReads++;
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, _activeReads);
            ReadCount++;
            _readTimes.Add(Stopwatch.GetElapsedTime(_startedAt));

            if (_currentStep is null && _steps.Count > 0)
            {
                _currentStep = _steps.Dequeue();
                _currentOffset = 0;
                _delayPending = true;
            }

            step = _currentStep;
            shouldDelay = _delayPending && step?.Delay is not null;
        }

        try
        {
            if (shouldDelay)
                await Task.Delay(step!.Delay!.Value, cancellationToken);

            lock (_gate)
            {
                _delayPending = false;
                if (step is null) return 0;

                if (step.Error is not null)
                {
                    _currentStep = null;
                    throw step.Error;
                }

                if (step.Content is null)
                {
                    // EOF chỉ tiêu thụ step hiện tại. Nếu script còn step byte, lần read sau
                    // sẽ lấy step đó — mô phỏng nguồn non-standard resume sau temporary EOF.
                    _currentStep = null;
                    return 0;
                }

                var count = Math.Min(buffer.Length, step.Content.Length - _currentOffset);
                step.Content.AsMemory(_currentOffset, count).CopyTo(buffer);
                _currentOffset += count;
                if (_currentOffset == step.Content.Length) _currentStep = null;
                return count;
            }
        }
        finally
        {
            lock (_gate) _activeReads--;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
