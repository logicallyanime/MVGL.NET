using System.Buffers.Binary;
using System.Text;

namespace MVGLTools;

internal static class Helpers
{
    private static readonly object LogLock = new();

    public static bool FileEquivalent(string first, string second)
    {
        try
        {
            var firstPath = Path.GetFullPath(first);
            var secondPath = Path.GetFullPath(second);
            return string.Equals(firstPath, secondPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void Log(string message)
    {
        lock (LogLock)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
        }
    }

    public static long CeilInteger(long value, long step)
    {
        if (step == 0)
        {
            return value;
        }

        return ((value + step - 1) / step) * step;
    }

    public static string TrimFixedString(ReadOnlySpan<byte> bytes)
    {
        var firstNull = bytes.IndexOf((byte)0);
        var firstSpace = bytes.IndexOf((byte)' ');
        var end = bytes.Length;

        if (firstNull >= 0)
        {
            end = Math.Min(end, firstNull);
        }

        if (firstSpace >= 0)
        {
            end = Math.Min(end, firstSpace);
        }

        return Encoding.ASCII.GetString(bytes[..end]);
    }

    public static string WrapRegex(string input) => $"^{input}$";

    public static uint GetChecksum(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                var mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return ~crc;
    }

    public static void AlignRead(Stream stream, long alignment)
    {
        var aligned = CeilInteger(stream.Position, alignment);
        if (aligned != stream.Position)
        {
            stream.Seek(aligned, SeekOrigin.Begin);
        }
    }

    public static void AlignWrite(Stream stream, long alignment)
    {
        var aligned = CeilInteger(stream.Position, alignment);
        while (stream.Position < aligned)
        {
            stream.WriteByte(0);
        }
    }

    public static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += read;
        }

        return buffer;
    }

    public static byte[] ReadBytes(FileStream stream, int count, bool crypt)
    {
        var offset = stream.Position;
        var buffer = ReadBytes((Stream)stream, count);
        if (crypt)
        {
            Mdb1Format.CryptArray(buffer, offset);
        }

        return buffer;
    }

    public static void WriteBytes(Stream stream, ReadOnlySpan<byte> data, bool crypt)
    {
        if (!crypt)
        {
            stream.Write(data);
            return;
        }

        var copy = data.ToArray();
        Mdb1Format.CryptArray(copy, stream.Position);
        stream.Write(copy);
    }

    public static uint ReadUInt32(FileStream stream, bool crypt) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(stream, 4, crypt));
    public static int ReadInt32(FileStream stream, bool crypt) => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(stream, 4, crypt));
    public static ushort ReadUInt16(FileStream stream, bool crypt) => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(stream, 2, crypt));
    public static byte ReadByte(FileStream stream, bool crypt) => ReadBytes(stream, 1, crypt)[0];
    public static ulong ReadUInt64(FileStream stream, bool crypt) => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(stream, 8, crypt));

    public static void WriteUInt32(Stream stream, uint value, bool crypt)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        WriteBytes(stream, buffer, crypt);
    }

    public static void WriteInt32(Stream stream, int value, bool crypt)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        WriteBytes(stream, buffer, crypt);
    }

    public static void WriteUInt16(Stream stream, ushort value, bool crypt)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        WriteBytes(stream, buffer, crypt);
    }

    public static void WriteUInt64(Stream stream, ulong value, bool crypt)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        WriteBytes(stream, buffer, crypt);
    }
}
