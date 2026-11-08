namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Fixed-capacity ring buffer of mono float samples addressed by an absolute, ever-increasing sample position.
/// Pure data structure (no audio APIs), allocation-free after construction. <b>Not thread-safe</b>:
/// callers that share an instance between threads must synchronize externally.
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;

    /// <summary>Creates a ring that keeps at least <paramref name="minimumCapacity"/> most recent samples.</summary>
    /// <param name="minimumCapacity">Requested capacity; rounded up to the next power of two.</param>
    public SampleRingBuffer(int minimumCapacity)
    {
        if (minimumCapacity < 1) throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        var capacity = 1;
        while (capacity < minimumCapacity) capacity <<= 1;
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Number of samples the ring can hold (power of two).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Total number of samples ever written; also the absolute position just after the newest sample.</summary>
    public long TotalWritten { get; private set; }

    /// <summary>Appends samples. When more than <see cref="Capacity"/> samples are given only the newest are kept.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        var skipped = 0;
        if (samples.Length > _buffer.Length)
        {
            skipped = samples.Length - _buffer.Length;
            samples = samples.Slice(skipped);
        }

        var start = (int)((TotalWritten + skipped) & _mask);
        var first = Math.Min(samples.Length, _buffer.Length - start);
        samples.Slice(0, first).CopyTo(_buffer.AsSpan(start, first));
        if (first < samples.Length)
            samples.Slice(first).CopyTo(_buffer.AsSpan(0, samples.Length - first));

        TotalWritten += skipped + samples.Length;
    }

    /// <summary>
    /// Copies the <c>destination.Length</c> samples that end at absolute position <paramref name="endPosition"/>
    /// (exclusive) into <paramref name="destination"/>. Positions that were never written, were already overwritten,
    /// or lie beyond <see cref="TotalWritten"/> are filled with zeros.
    /// </summary>
    public void CopyWindow(long endPosition, Span<float> destination)
    {
        var length = destination.Length;
        if (length == 0) return;

        var start = endPosition - length;
        var oldestValid = Math.Max(0L, TotalWritten - _buffer.Length);
        var newestValid = TotalWritten; // exclusive

        for (var i = 0; i < length;)
        {
            var pos = start + i;
            if (pos < oldestValid || pos >= newestValid)
            {
                destination[i] = 0f;
                i++;
                continue;
            }

            // Copy a contiguous run up to the end of valid data, the window, or the physical end of the array.
            var index = (int)(pos & _mask);
            var run = (int)Math.Min(Math.Min(length - i, newestValid - pos), _buffer.Length - index);
            _buffer.AsSpan(index, run).CopyTo(destination.Slice(i, run));
            i += run;
        }
    }

    /// <summary>Zeroes the stored samples without changing <see cref="TotalWritten"/>.</summary>
    public void Clear() => Array.Clear(_buffer);
}
