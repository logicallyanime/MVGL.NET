using System.Buffers.Binary;
using System.Text;

namespace MVGLTools;

/// <summary>
/// Provides helpers for encrypting and decrypting DSCS save files.
/// </summary>
public static class SaveFile
{
    /// <summary>
    /// Decrypts a save file and writes the decrypted result to a new file.
    /// </summary>
    /// <param name="source">The encrypted input save file.</param>
    /// <param name="target">The output path for the decrypted save file.</param>
    public static void Decrypt(string source, string target)
    {
        ValidatePaths(source, target);
        var buffer = File.ReadAllBytes(source);
        var (key1, val) = CalculateFileKey(Path.GetFileName(source));

        RotateStep(buffer, val, decrypt: true);
        XorMathStep(buffer, key1, val, decrypt: true);

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        File.WriteAllBytes(target, buffer);
    }

    /// <summary>
    /// Encrypts a save file and writes the encrypted result to a new file.
    /// </summary>
    /// <param name="source">The decrypted input save file.</param>
    /// <param name="target">The output path for the encrypted save file.</param>
    public static void Encrypt(string source, string target)
    {
        ValidatePaths(source, target);
        var buffer = File.ReadAllBytes(source);
        var (key1, val) = CalculateFileKey(Path.GetFileName(source));

        XorMathStep(buffer, key1, val, decrypt: false);
        RotateStep(buffer, val, decrypt: false);

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        File.WriteAllBytes(target, buffer);
    }

    private static void ValidatePaths(string source, string target)
    {
        if (Helpers.FileEquivalent(source, target))
        {
            throw new ArgumentException("Input and output path must be different.");
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source path is not a regular file.", source);
        }

        if (Directory.Exists(target))
        {
            throw new ArgumentException("Target path is not a regular file.", nameof(target));
        }
    }

    private static (byte[] Key, ulong Value) CalculateFileKey(string fileName)
    {
        const ulong staticKey = 0x1415926535897932UL;

        var dynamicKeyText = fileName.StartsWith("slot_", StringComparison.Ordinal)
            ? "@Tokomon"
            : fileName.Equals("system_data.bin", StringComparison.Ordinal)
                ? "@Dagomon"
                : "@Lilimon";

        var dynamicKey = BinaryPrimitives.ReadUInt64LittleEndian(Encoding.ASCII.GetBytes(dynamicKeyText));
        var value = dynamicKey ^ staticKey;
        var key = new byte[11];
        key[0] = (byte)(value >> 0x08);
        key[1] = (byte)(value >> 0x30);
        key[2] = (byte)(value >> 0x18);
        key[3] = (byte)(value >> 0x00);
        key[4] = (byte)(value >> 0x10);
        key[5] = (byte)(value >> 0x03);
        key[6] = (byte)(value >> 0x28);
        key[7] = (byte)(value >> 0x15);
        key[8] = (byte)(value >> 0x20);
        key[9] = (byte)(value >> 0x2F);
        key[10] = (byte)(value >> 0x38);
        return (key, value);
    }

    private static void RotateStep(byte[] buffer, ulong value, bool decrypt)
    {
        const ulong magic = 0x801302D26B3BEAE5UL;
        var initialVector = (ulong)(((UInt128)magic * value) >> 0x4E);
        var rotateParameter = (uint)((uint)value - (initialVector * 0x7FED));

        var offset = 0;
        var remaining = (uint)buffer.Length;

        while (remaining != 0)
        {
            var read = remaining < 16 ? remaining : 16;
            var tmp2 = (uint)((rotateParameter * (ulong)0x24924925) >> 32);
            var rotateCount = (int)(((((rotateParameter - tmp2) >> 1) + tmp2) >> 2) * 7 - rotateParameter - 1);

            ulong valueSum = 0;
            for (var i = 0; i < read; i++)
            {
                var current = buffer[offset + i];
                if (decrypt)
                {
                    for (var j = 0; j < -rotateCount; j++)
                    {
                        current = (byte)((current >> 1) | (current << 7));
                    }
                }

                valueSum += current;

                if (!decrypt)
                {
                    for (var j = 0; j < -rotateCount; j++)
                    {
                        current = (byte)((current << 1) | (current >> 7));
                    }
                }

                buffer[offset + i] = current;
            }

            var tmp = ((ulong)0x72C62A25 * valueSum) >> 0x28;
            tmp2 = (uint)((rotateParameter * 0x10DCDUL + 1) + ((valueSum - (tmp * 0x23B)) * 2));
            rotateParameter = (uint)(tmp2 - ((((UInt128)0x40004001 * tmp2) >> 0x3D) * 0x7FFF7FFF));

            offset += 0x10;
            remaining -= 0x10;
        }
    }

    private static void XorMathStep(byte[] buffer, byte[] key, ulong value, bool decrypt)
    {
        const ulong magic = 0x3B2153E7529FE1FFUL;
        var tmp = (ulong)(((UInt128)value * magic) >> 64);
        var init = (uint)(value - (((((value - tmp) >> 1) + tmp) >> 0xF) * 0xCFF7));
        uint charSum = 0;

        for (var i = 0; i < buffer.Length; i++)
        {
            var current = buffer[i];
            const ulong localMagic1 = 0xAB8F69E3UL;
            const ulong localMagic2 = 0x2E8BA2E9UL;
            var keyOffset = i - ((((uint)((localMagic2 * (ulong)i) >> 0x21)) + (((uint)((localMagic2 * (ulong)i) >> 0x21)) >> 0x1F)) * 0xB);

            if (decrypt)
            {
                current = (byte)(current - (byte)(((localMagic1 * charSum) >> 0x27) * 0x41));
                current = (byte)(current - (byte)charSum);
                current ^= key[keyOffset];
                current ^= (byte)init;
                buffer[i] = current;
                charSum += current;
            }
            else
            {
                current ^= (byte)init;
                current ^= key[keyOffset];
                current = (byte)(current + (byte)charSum);
                current = (byte)(current + (byte)(((localMagic1 * charSum) >> 0x27) * 0x41));
                charSum += buffer[i];
                buffer[i] = current;
            }

            var tmp2 = (init * 0x10DCDUL) + 0x0D;
            init = (uint)(tmp2 - ((((UInt128)tmp2 * 0x40004001) >> 0x3D) * 0x7FFF7FFF));
        }
    }
}
