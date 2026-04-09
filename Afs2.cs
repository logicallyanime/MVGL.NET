using System.Buffers.Binary;
using System.Globalization;

namespace MVGLTools;

/// <summary>
/// Provides helpers for extracting and creating <c>AFS2</c> archives.
/// </summary>
public static class Afs2
{
    private const uint Afs2MagicValue = 0x32534641;

    /// <summary>
    /// Extracts an <c>AFS2</c> archive into a directory of <c>.hca</c> files.
    /// </summary>
    /// <param name="source">The source archive path.</param>
    /// <param name="target">The output directory path.</param>
    public static void Extract(string source, string target)
    {
        if (File.Exists(target) && !Directory.Exists(target))
        {
            throw new ArgumentException("Target path exists and is not a directory.", nameof(target));
        }

        if (!File.Exists(source))
        {
            throw new ArgumentException("Source path does not point to a file.", nameof(source));
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = Helpers.ReadBytes(input, 16);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));

        if (magic != Afs2MagicValue)
        {
            throw new InvalidDataException("Source file is not an AFS2 archive.");
        }

        _ = Helpers.ReadBytes(input, checked((int)fileCount * 2));
        var offsetsBuffer = Helpers.ReadBytes(input, checked(((int)fileCount + 1) * 4));
        var offsets = new uint[fileCount + 1];
        for (var i = 0; i < offsets.Length; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(offsetsBuffer.AsSpan(i * 4, 4));
        }

        if (input.Position < blockSize)
        {
            input.Seek(blockSize, SeekOrigin.Begin);
        }

        if (input.Position != offsets[0])
        {
            throw new InvalidDataException("AFS2 header ended at an unexpected position.");
        }

        Directory.CreateDirectory(target);

        for (var i = 0; i < fileCount; i++)
        {
            var aligned = Helpers.CeilInteger(input.Position, blockSize);
            if (aligned != input.Position)
            {
                input.Seek(aligned, SeekOrigin.Begin);
            }

            var size = checked((int)(offsets[i + 1] - input.Position));
            var data = Helpers.ReadBytes(input, size);
            var fileName = i.ToString("x6", CultureInfo.InvariantCulture) + ".hca";
            File.WriteAllBytes(Path.Combine(target, fileName), data);
        }
    }

    /// <summary>
    /// Creates an <c>AFS2</c> archive from the files in a directory.
    /// </summary>
    /// <param name="source">The source directory containing files to pack.</param>
    /// <param name="target">The output archive path.</param>
    public static void Pack(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            throw new ArgumentException("Source path is not a directory.", nameof(source));
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var files = Directory.EnumerateFiles(source)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

        const uint flags = 0x00020402;
        const int blockSize = 0x20;
        var fileCount = checked((uint)files.Length);

        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Afs2MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], fileCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], blockSize);
        output.Write(header);

        var ids = new ushort[fileCount];
        var offsets = new uint[fileCount + 1];
        offsets[0] = Math.Max(checked(0x10u + fileCount * 0x06u + 4u), (uint)blockSize);

        for (var i = 0; i < files.Length; i++)
        {
            var aligned = Helpers.CeilInteger(offsets[i], blockSize);
            output.Seek(aligned, SeekOrigin.Begin);

            var data = File.ReadAllBytes(files[i]);
            output.Write(data);

            ids[i] = checked((ushort)i);
            offsets[i + 1] = checked((uint)output.Position);
        }

        output.Seek(0x10, SeekOrigin.Begin);
        Span<byte> shortBuffer = stackalloc byte[2];
        foreach (var id in ids)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(shortBuffer, id);
            output.Write(shortBuffer);
        }

        Span<byte> intBuffer = stackalloc byte[4];
        foreach (var offset in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, offset);
            output.Write(intBuffer);
        }
    }
}
