using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Converts interleaved WASAPI buffers (IEEE float 32/64-bit, PCM 8/16/24/32-bit, plain or WAVE_FORMAT_EXTENSIBLE)
/// into mono floats in −1..1 by averaging channels. Allocation-free per call. Audio is never stored or written.
/// </summary>
internal sealed class SampleConverter
{
    // KSDATAFORMAT_SUBTYPE_PCM / KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private enum Kind
    {
        Float32,
        Float64,
        Pcm8,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    private readonly Kind _kind;
    private readonly int _bytesPerSample;

    private SampleConverter(Kind kind, int channels, int sampleRate, int blockAlign, int bytesPerSample)
    {
        _kind = kind;
        Channels = channels;
        SampleRate = sampleRate;
        BlockAlign = blockAlign;
        _bytesPerSample = bytesPerSample;
    }

    /// <summary>Interleaved channel count of the source.</summary>
    public int Channels { get; }

    /// <summary>Sample rate, Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Bytes per interleaved frame.</summary>
    public int BlockAlign { get; }

    /// <summary>Human-readable description for logs.</summary>
    public string Description => $"{_kind}, {Channels} ch, {SampleRate} Hz";

    /// <summary>
    /// Builds a converter for <paramref name="format"/>.
    /// </summary>
    /// <returns>The converter, or null with a Russian <paramref name="error"/> when the format is unsupported.</returns>
    public static SampleConverter? TryCreate(WaveFormat format, out string? error)
    {
        error = null;
        var channels = format.Channels;
        var bits = format.BitsPerSample;
        var sampleRate = format.SampleRate;
        if (channels < 1 || sampleRate < 8000 || bits < 8 || bits % 8 != 0)
        {
            error = $"Неподдерживаемый формат звука ({format.Encoding}, {bits} бит, {channels} кан., {sampleRate} Гц).";
            return null;
        }

        var bytesPerSample = bits / 8;
        var blockAlign = format.BlockAlign > 0 ? format.BlockAlign : bytesPerSample * channels;
        if (blockAlign < bytesPerSample * channels)
        {
            error = "Некорректное выравнивание аудиоблока.";
            return null;
        }

        bool isFloat;
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                isFloat = true;
                break;
            case WaveFormatEncoding.Pcm:
                isFloat = false;
                break;
            case WaveFormatEncoding.Extensible:
                if (format is WaveFormatExtensible extensible)
                {
                    var sub = extensible.SubFormat;
                    if (sub == SubtypeIeeeFloat) isFloat = true;
                    else if (sub == SubtypePcm) isFloat = false;
                    else
                    {
                        error = $"Неподдерживаемый подформат звука ({sub}).";
                        return null;
                    }
                }
                else
                {
                    // Shared-mode mix formats are IEEE float in practice; 32-bit without sub-format info → float.
                    isFloat = bits == 32 || bits == 64;
                }
                break;
            default:
                error = $"Неподдерживаемая кодировка звука ({format.Encoding}).";
                return null;
        }

        Kind kind;
        if (isFloat)
        {
            if (bits == 32) kind = Kind.Float32;
            else if (bits == 64) kind = Kind.Float64;
            else
            {
                error = $"Неподдерживаемая разрядность float ({bits} бит).";
                return null;
            }
        }
        else
        {
            switch (bits)
            {
                case 8: kind = Kind.Pcm8; break;
                case 16: kind = Kind.Pcm16; break;
                case 24: kind = Kind.Pcm24; break;
                case 32: kind = Kind.Pcm32; break; // also 24-in-32 containers (left-justified)
                default:
                    error = $"Неподдерживаемая разрядность PCM ({bits} бит).";
                    return null;
            }
        }

        return new SampleConverter(kind, channels, sampleRate, blockAlign, bytesPerSample);
    }

    /// <summary>Number of whole frames contained in <paramref name="byteCount"/> bytes.</summary>
    public int FrameCount(int byteCount) => byteCount <= 0 ? 0 : byteCount / BlockAlign;

    /// <summary>
    /// Downmixes the whole frames of <paramref name="source"/> into <paramref name="destination"/>
    /// (must hold at least <see cref="FrameCount"/> values). Non-finite samples become 0.
    /// </summary>
    /// <returns>Number of mono samples written.</returns>
    public int ToMono(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var frames = Math.Min(FrameCount(source.Length), destination.Length);
        if (frames == 0) return 0;

        var channels = Channels;
        var inv = 1f / channels;
        var block = BlockAlign;

        switch (_kind)
        {
            case Kind.Float32 when block == 4 * channels:
            {
                var src = MemoryMarshal.Cast<byte, float>(source.Slice(0, frames * block));
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++, i++) sum += src[i];
                    destination[f] = Sanitize(sum * inv);
                }
                break;
            }
            case Kind.Pcm16 when block == 2 * channels:
            {
                var src = MemoryMarshal.Cast<byte, short>(source.Slice(0, frames * block));
                const float scale = 1f / 32768f;
                for (int f = 0, i = 0; f < frames; f++)
                {
                    var sum = 0;
                    for (var c = 0; c < channels; c++, i++) sum += src[i];
                    destination[f] = sum * scale * inv;
                }
                break;
            }
            default:
            {
                // Generic path: any block alignment / container (handles padding bytes too).
                for (var f = 0; f < frames; f++)
                {
                    var frame = source.Slice(f * block, block);
                    float sum = 0;
                    for (var c = 0; c < channels; c++)
                        sum += ReadSample(frame.Slice(c * _bytesPerSample, _bytesPerSample));
                    destination[f] = Sanitize(sum * inv);
                }
                break;
            }
        }

        return frames;
    }

    private float ReadSample(ReadOnlySpan<byte> s)
    {
        switch (_kind)
        {
            case Kind.Float32:
                return MemoryMarshal.Read<float>(s);
            case Kind.Float64:
                return (float)MemoryMarshal.Read<double>(s);
            case Kind.Pcm8:
                return (s[0] - 128) / 128f;
            case Kind.Pcm16:
                return MemoryMarshal.Read<short>(s) / 32768f;
            case Kind.Pcm24:
                return (s[0] | (s[1] << 8) | ((sbyte)s[2] << 16)) / 8388608f;
            default:
                return MemoryMarshal.Read<int>(s) / 2147483648f;
        }
    }

    private static float Sanitize(float v) => float.IsFinite(v) ? v : 0f;
}
