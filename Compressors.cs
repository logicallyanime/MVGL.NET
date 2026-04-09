using Doboz;
using K4os.Compression.LZ4;

namespace MVGLTools;

/// <summary>
/// Defines compression operations used by archive profiles.
/// </summary>
public interface ICompressor
{
    /// <summary>
    /// Decompresses the provided byte array.
    /// </summary>
    /// <param name="input">The compressed input data.</param>
    /// <param name="expectedSize">The expected decompressed size in bytes.</param>
    /// <returns>The decompressed data, or the original input when the implementation treats it as already uncompressed.</returns>
    byte[] Decompress(byte[] input, int expectedSize);

    /// <summary>
    /// Compresses the provided byte array.
    /// </summary>
    /// <param name="input">The input data to compress.</param>
    /// <returns>The compressed data.</returns>
    byte[] Compress(byte[] input);

    /// <summary>
    /// Determines whether the supplied data appears to be compressed with the current codec.
    /// </summary>
    /// <param name="input">The data to inspect.</param>
    /// <returns><see langword="true"/> if the data appears compressed; otherwise, <see langword="false"/>.</returns>
    bool IsCompressed(byte[] input);
}

/// <summary>
/// Implements the Doboz compression codec used by DSCS profiles.
/// </summary>
public sealed class DobozCompressor : ICompressor
{
    /// <inheritdoc />
    public byte[] Decompress(byte[] input, int expectedSize)
    {
        int uncompressedSize;
        try
        {
            uncompressedSize = DobozCodec.UncompressedLength(input, 0, input.Length);
        }
        catch
        {
            return input;
        }

        if (uncompressedSize <= 0 || uncompressedSize != expectedSize)
        {
            return input;
        }

        try
        {
            return DobozCodec.Decode(input, 0, input.Length);
        }
        catch
        {
            return input;
        }
    }

    /// <inheritdoc />
    public byte[] Compress(byte[] input)
    {
        return DobozCodec.Encode(input, 0, input.Length);
    }

    /// <inheritdoc />
    public bool IsCompressed(byte[] input)
    {
        if (input.Length < 8)
        {
            return false;
        }

        try
        {
            var uncompressedLength = DobozCodec.UncompressedLength(input, 0, input.Length);
            if (uncompressedLength <= 0)
            {
                return false;
            }

            var output = new byte[uncompressedLength];
            var decoded = DobozCodec.Decode(input, 0, input.Length, output, 0, output.Length);
            if (decoded != uncompressedLength)
            {
                return false;
            }

            var recompressed = DobozCodec.Encode(output, 0, output.Length);
            return recompressed.Length == input.Length && recompressed.AsSpan().SequenceEqual(input);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Implements the LZ4 compression codec used by DSTS and THL profiles.
/// </summary>
public sealed class Lz4Compressor : ICompressor
{
    /// <inheritdoc />
    public byte[] Decompress(byte[] input, int expectedSize)
    {
        if (input.Length == expectedSize)
        {
            return input;
        }

        var output = new byte[expectedSize];
        var decoded = LZ4Codec.Decode(input, 0, input.Length, output, 0, output.Length);
        if (decoded != expectedSize)
        {
            throw new InvalidDataException("LZ4 decompression failed.");
        }

        return output;
    }

    /// <inheritdoc />
    public byte[] Compress(byte[] input)
    {
        var maxSize = LZ4Codec.MaximumOutputSize(input.Length);
        var output = new byte[maxSize];
        var encoded = LZ4Codec.Encode(input, 0, input.Length, output, 0, output.Length, LZ4Level.L12_MAX);
        if (encoded <= 0)
        {
            throw new InvalidDataException("LZ4 compression failed.");
        }

        Array.Resize(ref output, encoded);
        return output;
    }

    /// <inheritdoc />
    public bool IsCompressed(byte[] input)
    {
        var output = new byte[256];
        try
        {
            return LZ4Codec.Decode(input, 0, input.Length, output, 0, output.Length) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
