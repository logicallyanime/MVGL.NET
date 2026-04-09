using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MVGLTools;

/// <summary>
/// Identifies the data type of an <c>EXPA</c> field.
/// </summary>
public enum EntryType : uint
{
    /// <summary>
    /// A variable-length array of 32-bit signed integers.
    /// </summary>
    Int32Array = 0,

    /// <summary>
    /// An unknown field type observed in game data.
    /// </summary>
    Unk1 = 1,

    /// <summary>
    /// A 32-bit signed integer.
    /// </summary>
    Int32 = 2,

    /// <summary>
    /// A 16-bit signed integer.
    /// </summary>
    Int16 = 3,

    /// <summary>
    /// An 8-bit signed integer.
    /// </summary>
    Int8 = 4,

    /// <summary>
    /// A 32-bit floating-point value.
    /// </summary>
    Float = 5,

    /// <summary>
    /// A string field using the third observed string encoding/semantics variant.
    /// </summary>
    String3 = 6,

    /// <summary>
    /// A string field using the primary observed string encoding/semantics variant.
    /// </summary>
    String = 7,

    /// <summary>
    /// A string field using the second observed string encoding/semantics variant.
    /// </summary>
    String2 = 8,

    /// <summary>
    /// A packed Boolean field.
    /// </summary>
    Bool = 9,

    /// <summary>
    /// An empty or placeholder field.
    /// </summary>
    Empty = 10,
}

/// <summary>
/// Represents one chunk entry referenced by an <c>EXPA</c> row.
/// </summary>
/// <param name="Offset">The absolute or row-relative offset associated with the chunk.</param>
/// <param name="Value">The chunk payload.</param>
public readonly record struct ChnkEntry(uint Offset, byte[] Value);

/// <summary>
/// Represents a serialized <c>EXPA</c> row and its dependent chunk data.
/// </summary>
/// <param name="Data">The fixed-size row data.</param>
/// <param name="Chunk">The chunk entries referenced by the row.</param>
public readonly record struct ExpaEntry(byte[] Data, IReadOnlyList<ChnkEntry> Chunk);

/// <summary>
/// Describes one field in a table structure.
/// </summary>
/// <param name="Name">The field name.</param>
/// <param name="Type">The field type.</param>
public readonly record struct StructureEntry(string Name, EntryType Type);

/// <summary>
/// Represents one logical table in an <c>EXPA</c> file.
/// </summary>
/// <param name="Name">The table name.</param>
/// <param name="Structure">The structure describing each row.</param>
/// <param name="Entries">The table rows.</param>
public readonly record struct Table(string Name, Structure Structure, IReadOnlyList<IReadOnlyList<object?>> Entries);

/// <summary>
/// Represents a full <c>EXPA</c> file containing one or more tables.
/// </summary>
/// <param name="Tables">The tables stored in the file.</param>
public readonly record struct TableFile(IReadOnlyList<Table> Tables);

/// <summary>
/// Describes profile-specific behavior for reading and writing <c>EXPA</c> files.
/// </summary>
public interface IExpaProfile
{
    /// <summary>
    /// Gets the alignment step used when reading table metadata.
    /// </summary>
    int AlignStep { get; }

    /// <summary>
    /// Gets a value indicating whether the profile stores structure information in the binary file.
    /// </summary>
    bool HasStructureSection { get; }

    /// <summary>
    /// Gets the relative folder used for external structure files.
    /// </summary>
    string StructureFolder { get; }
}

/// <summary>
/// <c>EXPA</c> profile for DSCS.
/// </summary>
public sealed class DscsExpaProfile : IExpaProfile
{
    /// <inheritdoc />
    public int AlignStep => 4;

    /// <inheritdoc />
    public bool HasStructureSection => false;

    /// <inheritdoc />
    public string StructureFolder => "structures/dscs";
}

/// <summary>
/// <c>EXPA</c> profile for DSTS.
/// </summary>
public sealed class DstsExpaProfile : IExpaProfile
{
    /// <inheritdoc />
    public int AlignStep => 8;

    /// <inheritdoc />
    public bool HasStructureSection => true;

    /// <inheritdoc />
    public string StructureFolder => "structures/dsts";
}

/// <summary>
/// <c>EXPA</c> profile for THL.
/// </summary>
public sealed class ThlExpaProfile : IExpaProfile
{
    /// <inheritdoc />
    public int AlignStep => 8;

    /// <inheritdoc />
    public bool HasStructureSection => true;

    /// <inheritdoc />
    public string StructureFolder => "structures/tlh";
}

/// <summary>
/// Represents the schema for one <c>EXPA</c> table.
/// </summary>
public sealed class Structure
{
    private readonly IReadOnlyList<StructureEntry> _entries;

    /// <summary>
    /// Initializes a new table structure.
    /// </summary>
    /// <param name="entries">The ordered field definitions.</param>
    public Structure(IEnumerable<StructureEntry> entries)
    {
        _entries = entries.ToArray();
    }

    /// <summary>
    /// Gets the ordered field definitions.
    /// </summary>
    public IReadOnlyList<StructureEntry> Entries => _entries;

    /// <summary>
    /// Gets the number of fields in the structure.
    /// </summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Serializes one logical row into its binary <c>EXPA</c> representation.
    /// </summary>
    /// <param name="entries">The row values matching the structure order.</param>
    /// <returns>The serialized row data and referenced chunk entries.</returns>
    public ExpaEntry WriteExpa(IReadOnlyList<object?> entries)
    {
        var offset = 0u;
        var bitCounter = 0;
        var currentBool = 0u;
        var chunkEntries = new List<ChnkEntry>();
        var data = Enumerable.Repeat((byte)0xCC, checked((int)GetExpaSize())).ToArray();

        for (var i = 0; i < _entries.Count; i++)
        {
            var type = _entries[i].Type;
            var entry = entries[i];

            if (type != EntryType.Bool || bitCounter >= 32)
            {
                if (bitCounter > 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset, 4), currentBool);
                    offset += 4;
                    bitCounter = 0;
                    currentBool = 0;
                }

                offset = (uint)Helpers.CeilInteger(offset, GetAlignment(type));
            }

            var chunk = WriteEntry(offset, data, type, entry);
            if (chunk is not null)
            {
                chunkEntries.Add(chunk.Value);
            }

            if (type == EntryType.Bool)
            {
                if (Convert.ToBoolean(entry, CultureInfo.InvariantCulture))
                {
                    currentBool |= 1u << bitCounter;
                }

                bitCounter++;
            }
            else
            {
                offset += GetSize(type);
            }
        }

        if (bitCounter > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset, 4), currentBool);
        }

        return new ExpaEntry(data, chunkEntries);
    }

    /// <summary>
    /// Reads one logical row from binary <c>EXPA</c> data.
    /// </summary>
    /// <param name="data">The row data slice.</param>
    /// <param name="baseOffset">The absolute offset of the row data in the file.</param>
    /// <param name="chunks">The chunk data indexed by offset.</param>
    /// <returns>The parsed row values.</returns>
    public IReadOnlyList<object?> ReadExpa(ReadOnlySpan<byte> data, uint baseOffset, IReadOnlyDictionary<uint, byte[]> chunks)
    {
        if (_entries.Count == 0)
        {
            return Array.Empty<object?>();
        }

        var values = new List<object?>(_entries.Count);
        var offset = 0u;
        var bitCounter = 0u;

        foreach (var entry in _entries)
        {
            if (entry.Type != EntryType.Bool || bitCounter >= 32)
            {
                if (bitCounter > 0)
                {
                    offset += GetSize(EntryType.Bool);
                }

                offset = (uint)Helpers.CeilInteger(offset, GetAlignment(entry.Type));
                bitCounter = 0;
            }

            values.Add(ReadEntry(entry.Type, data[(int)offset..], baseOffset + offset, bitCounter, chunks));

            if (entry.Type == EntryType.Bool)
            {
                bitCounter++;
            }
            else
            {
                offset += GetSize(entry.Type);
            }
        }

        return values;
    }

    /// <summary>
    /// Converts a CSV row into typed values that match the structure.
    /// </summary>
    /// <param name="data">The raw CSV field values.</param>
    /// <returns>The converted row values.</returns>
    public IReadOnlyList<object?> ReadCsv(IReadOnlyList<string> data)
    {
        var values = new object?[_entries.Count];
        for (var i = 0; i < _entries.Count; i++)
        {
            values[i] = GetCsvValue(_entries[i].Type, data[i]);
        }

        return values;
    }

    /// <summary>
    /// Builds the CSV header line for the structure.
    /// </summary>
    /// <returns>The CSV header string.</returns>
    public string GetCsvHeader() => string.Join(',', _entries.Select(static entry => entry.Name));

    /// <summary>
    /// Converts a typed row into one CSV line.
    /// </summary>
    /// <param name="entries">The typed row values.</param>
    /// <returns>The serialized CSV line.</returns>
    public string WriteCsv(IReadOnlyList<object?> entries)
    {
        var values = new string[_entries.Count];
        for (var i = 0; i < _entries.Count; i++)
        {
            values[i] = GetCsvString(_entries[i].Type, entries[i]);
        }

        return string.Join(',', values);
    }

    /// <summary>
    /// Calculates the serialized row size for this structure.
    /// </summary>
    /// <returns>The row size in bytes.</returns>
    public uint GetExpaSize()
    {
        if (_entries.Count == 0)
        {
            return 0;
        }

        var currentSize = 0u;
        var bitCounter = 0u;
        foreach (var entry in _entries)
        {
            if (bitCounter == 0 || bitCounter >= 32 || entry.Type != EntryType.Bool)
            {
                currentSize = (uint)Helpers.CeilInteger(currentSize, GetAlignment(entry.Type));
                bitCounter = 0;
            }

            if (bitCounter == 0)
            {
                currentSize += GetSize(entry.Type);
            }

            if (entry.Type == EntryType.Bool)
            {
                bitCounter++;
            }
        }

        return (uint)Helpers.CeilInteger(currentSize, 8);
    }

    private static ChnkEntry? WriteEntry(uint baseOffset, byte[] data, EntryType type, object? value)
    {
        switch (type)
        {
            case EntryType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan((int)baseOffset, 4), Convert.ToInt32(value, CultureInfo.InvariantCulture));
                return null;
            case EntryType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((int)baseOffset, 2), Convert.ToInt16(value, CultureInfo.InvariantCulture));
                return null;
            case EntryType.Int8:
                data[baseOffset] = unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture));
                return null;
            case EntryType.Float:
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((int)baseOffset, 4), Convert.ToSingle(value, CultureInfo.InvariantCulture));
                return null;
            case EntryType.String:
            case EntryType.String2:
            case EntryType.String3:
            {
                data.AsSpan((int)baseOffset, 8).Clear();
                var text = value as string ?? string.Empty;
                if (text.Length == 0)
                {
                    return null;
                }

                var bytes = new byte[Helpers.CeilInteger(Encoding.UTF8.GetByteCount(text) + 2, 4)];
                Encoding.UTF8.GetBytes(text, bytes);
                return new ChnkEntry(baseOffset, bytes);
            }
            case EntryType.Int32Array:
            {
                var array = (value as IReadOnlyList<int>) ?? ((IEnumerable<int>?)value)?.ToArray() ?? Array.Empty<int>();
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan((int)baseOffset, 4), array.Count);
                data.AsSpan((int)baseOffset + 8, 8).Clear();
                if (array.Count == 0)
                {
                    return null;
                }

                var bytes = new byte[array.Count * sizeof(int)];
                for (var i = 0; i < array.Count; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), array[i]);
                }

                return new ChnkEntry(baseOffset + 8, bytes);
            }
            default:
                return null;
        }
    }

    private static object? ReadEntry(EntryType type, ReadOnlySpan<byte> data, uint absoluteOffset, uint bitCounter, IReadOnlyDictionary<uint, byte[]> chunks)
    {
        return type switch
        {
            EntryType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data[..4]),
            EntryType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data[..2]),
            EntryType.Int8 => unchecked((sbyte)data[0]),
            EntryType.Float => BinaryPrimitives.ReadSingleLittleEndian(data[..4]),
            EntryType.String or EntryType.String2 or EntryType.String3 => chunks.TryGetValue(absoluteOffset, out var stringBytes)
                ? ReadNullTerminatedString(stringBytes)
                : string.Empty,
            EntryType.Bool => ((BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) >> (int)bitCounter) & 1u) == 1u,
            EntryType.Int32Array => ReadIntArray(data, absoluteOffset, chunks),
            _ => null,
        };
    }

    private static IReadOnlyList<int> ReadIntArray(ReadOnlySpan<byte> data, uint absoluteOffset, IReadOnlyDictionary<uint, byte[]> chunks)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        if (count <= 0 || !chunks.TryGetValue(absoluteOffset + 8, out var chunk))
        {
            return Array.Empty<int>();
        }

        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(i * 4, 4));
        }

        return values;
    }

    private static string ReadNullTerminatedString(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    private static string GetCsvString(EntryType type, object? value)
    {
        return type switch
        {
            EntryType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            EntryType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            EntryType.Int8 => Convert.ToSByte(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            EntryType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            EntryType.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false",
            EntryType.String or EntryType.String2 or EntryType.String3 => QuoteCsv(value as string ?? string.Empty),
            EntryType.Int32Array => string.Join(' ', ((IEnumerable<int>?)value) ?? Array.Empty<int>()),
            _ => string.Empty,
        };
    }

    private static object? GetCsvValue(EntryType type, string value)
    {
        return type switch
        {
            EntryType.Int32 => int.Parse(value, CultureInfo.InvariantCulture),
            EntryType.Int16 => short.Parse(value, CultureInfo.InvariantCulture),
            EntryType.Int8 => sbyte.Parse(value, CultureInfo.InvariantCulture),
            EntryType.Float => float.Parse(value, CultureInfo.InvariantCulture),
            EntryType.String or EntryType.String2 or EntryType.String3 => value,
            EntryType.Bool => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
            EntryType.Int32Array => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray(),
            _ => null,
        };
    }

    private static string QuoteCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static uint GetAlignment(EntryType type) => type switch
    {
        EntryType.Int32 => 4,
        EntryType.Int16 => 2,
        EntryType.Int8 => 1,
        EntryType.Float => 4,
        EntryType.String or EntryType.String2 or EntryType.String3 => 8,
        EntryType.Bool => 4,
        EntryType.Int32Array => 8,
        _ => 0,
    };

    private static uint GetSize(EntryType type) => type switch
    {
        EntryType.Int32 => 4,
        EntryType.Int16 => 2,
        EntryType.Int8 => 1,
        EntryType.Float => 4,
        EntryType.String or EntryType.String2 or EntryType.String3 => 8,
        EntryType.Bool => 4,
        EntryType.Int32Array => 16,
        _ => 0,
    };
}

/// <summary>
/// Provides helpers for reading, writing, importing, and exporting <c>EXPA</c> files.
/// </summary>
public static class Expa
{
    private const uint ExpaMagic = 0x41505845;
    private const uint ChnkMagic = 0x4B4E4843;

    /// <summary>
    /// Exports an <see cref="TableFile"/> to a folder of CSV files.
    /// </summary>
    /// <param name="file">The table file to export.</param>
    /// <param name="target">The destination directory.</param>
    public static void ExportCsv(TableFile file, string target)
    {
        if (File.Exists(target) && !Directory.Exists(target))
        {
            throw new ArgumentException("Target path exists and is not a directory.", nameof(target));
        }

        Directory.CreateDirectory(target);
        for (var index = 0; index < file.Tables.Count; index++)
        {
            var table = file.Tables[index];
            var path = Path.Combine(target, $"{index:000}_{table.Name}.csv");
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine(table.Structure.GetCsvHeader());
            foreach (var row in table.Entries)
            {
                writer.WriteLine(table.Structure.WriteCsv(row));
            }
        }
    }

    /// <summary>
    /// Imports a folder of CSV files into a <see cref="TableFile"/>.
    /// </summary>
    /// <typeparam name="TProfile">The profile used to resolve structure information.</typeparam>
    /// <param name="source">The source directory containing CSV files.</param>
    /// <returns>The imported table file.</returns>
    public static TableFile ImportCsv<TProfile>(string source)
        where TProfile : IExpaProfile, new()
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("Source path does not exist or is not a directory.");
        }

        var profile = new TProfile();
        var files = Directory.EnumerateFiles(source).OrderBy(static file => file, StringComparer.OrdinalIgnoreCase).ToArray();
        var tables = new List<Table>(files.Length);

        foreach (var file in files)
        {
            var csv = CsvFile.Read(file);
            var name = Path.GetFileNameWithoutExtension(file)[4..];
            var structure = GetStructureForCsv(profile, csv, source, name);
            var entries = csv.Rows.Select(structure.ReadCsv).ToArray();
            tables.Add(new Table(name, structure, entries));
        }

        return new TableFile(tables);
    }

    /// <summary>
    /// Writes a <see cref="TableFile"/> to an <c>EXPA</c> file.
    /// </summary>
    /// <typeparam name="TProfile">The profile that controls binary layout behavior.</typeparam>
    /// <param name="file">The table file to write.</param>
    /// <param name="path">The destination file path.</param>
    public static void Write<TProfile>(TableFile file, string path)
        where TProfile : IExpaProfile, new()
    {
        var profile = new TProfile();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Helpers.WriteUInt32(stream, ExpaMagic, false);
        Helpers.WriteUInt32(stream, checked((uint)file.Tables.Count), false);

        var chunkEntries = new List<ChnkEntry>();
        foreach (var table in file.Tables)
        {
            var structure = table.Structure;
            var nameBytes = Encoding.UTF8.GetBytes(table.Name + "\0");
            var nameSize = checked((int)Helpers.CeilInteger(nameBytes.Length, 4));
            var paddedName = new byte[nameSize];
            Array.Copy(nameBytes, paddedName, nameBytes.Length);

            Helpers.WriteInt32(stream, nameSize, false);
            Helpers.WriteBytes(stream, paddedName, false);

            if (profile.HasStructureSection)
            {
                Helpers.WriteUInt32(stream, checked((uint)structure.EntryCount), false);
                foreach (var entry in structure.Entries)
                {
                    Helpers.WriteUInt32(stream, (uint)entry.Type, false);
                }
            }

            var structureSize = structure.GetExpaSize();
            Helpers.WriteUInt32(stream, structureSize, false);
            Helpers.WriteUInt32(stream, checked((uint)table.Entries.Count), false);
            Helpers.AlignWrite(stream, 8);

            foreach (var entry in table.Entries)
            {
                var start = checked((uint)stream.Position);
                var result = structure.WriteExpa(entry);
                Helpers.WriteBytes(stream, result.Data, false);
                chunkEntries.AddRange(result.Chunk.Select(chunk => new ChnkEntry(chunk.Offset + start, chunk.Value)));
            }
        }

        Helpers.WriteUInt32(stream, ChnkMagic, false);
        Helpers.WriteUInt32(stream, checked((uint)chunkEntries.Count), false);
        foreach (var entry in chunkEntries)
        {
            Helpers.WriteUInt32(stream, entry.Offset, false);
            Helpers.WriteUInt32(stream, checked((uint)entry.Value.Length), false);
            Helpers.WriteBytes(stream, entry.Value, false);
        }
    }

    /// <summary>
    /// Reads an <c>EXPA</c> file from disk.
    /// </summary>
    /// <typeparam name="TProfile">The profile that controls binary layout behavior.</typeparam>
    /// <param name="path">The source file path.</param>
    /// <returns>The parsed table file.</returns>
    public static TableFile Read<TProfile>(string path)
        where TProfile : IExpaProfile, new()
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Source path does not exist.", path);
        }

        var profile = new TProfile();
        var content = File.ReadAllBytes(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (Helpers.ReadUInt32(stream, false) != ExpaMagic)
        {
            throw new InvalidDataException("Source file lacks EXPA header.");
        }

        var tableCount = Helpers.ReadInt32(stream, false);
        var tableEntries = new List<TableLayout>(tableCount);

        for (var i = 0; i < tableCount; i++)
        {
            Helpers.AlignRead(stream, profile.AlignStep);
            var nameLength = Helpers.ReadUInt32(stream, false);
            var nameBytes = Helpers.ReadBytes(stream, checked((int)nameLength));
            var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
            var structure = GetStructureForBinary(profile, stream, path, name);
            var entrySize = Helpers.ReadUInt32(stream, false);
            var entryCount = Helpers.ReadUInt32(stream, false);
            Helpers.AlignRead(stream, 8);

            var dataOffset = checked((uint)stream.Position);
            var structureSize = structure.GetExpaSize();
            if (structureSize != Helpers.CeilInteger(entrySize, 8))
            {
                throw new InvalidDataException($"Structure size {structureSize} does not match entry size {entrySize}.");
            }

            tableEntries.Add(new TableLayout(name, dataOffset, entryCount, entrySize, structure));
            stream.Seek((long)entryCount * Helpers.CeilInteger(entrySize, 8), SeekOrigin.Current);
        }

        Helpers.AlignRead(stream, profile.AlignStep);
        if (Helpers.ReadUInt32(stream, false) != ChnkMagic)
        {
            throw new InvalidDataException("Source file lacks CHNK header.");
        }

        var chnkCount = Helpers.ReadUInt32(stream, false);
        var chunks = new Dictionary<uint, byte[]>();
        for (var i = 0; i < chnkCount; i++)
        {
            var offset = Helpers.ReadUInt32(stream, false);
            var size = Helpers.ReadUInt32(stream, false);
            chunks[offset] = Helpers.ReadBytes(stream, checked((int)size));
        }

        var tables = new List<Table>(tableEntries.Count);
        foreach (var table in tableEntries)
        {
            var entries = new List<IReadOnlyList<object?>>(checked((int)table.EntryCount));
            var stride = checked((uint)Helpers.CeilInteger(table.EntrySize, 8));
            var offset = table.DataOffset;
            for (var i = 0; i < table.EntryCount; i++)
            {
                var entryData = content.AsSpan((int)offset, (int)stride);
                entries.Add(table.Structure.ReadExpa(entryData, offset, chunks));
                offset += stride;
            }

            tables.Add(new Table(table.Name, table.Structure, entries));
        }

        return new TableFile(tables);
    }

    private readonly record struct TableLayout(string Name, uint DataOffset, uint EntryCount, uint EntrySize, Structure Structure);

    private static Structure GetStructureForBinary(IExpaProfile profile, FileStream stream, string filePath, string tableName)
    {
        var fromFile = GetStructureFromFile(profile, filePath, tableName);
        if (!profile.HasStructureSection)
        {
            return new Structure(fromFile);
        }

        var count = Helpers.ReadUInt32(stream, false);
        var runtimeEntries = new StructureEntry[count];
        for (var i = 0; i < count; i++)
        {
            var type = (EntryType)Helpers.ReadUInt32(stream, false);
            runtimeEntries[i] = new StructureEntry($"{ToString(type)} {i}", type);
        }

        if (fromFile.Count != count)
        {
            return new Structure(runtimeEntries);
        }

        for (var i = 0; i < count; i++)
        {
            if (fromFile[i].Type != runtimeEntries[i].Type)
            {
                return new Structure(runtimeEntries);
            }
        }

        return new Structure(fromFile);
    }

    private static Structure GetStructureForCsv(IExpaProfile profile, CsvFile csv, string filePath, string tableName)
    {
        var structure = csv.Header.Select(header => new StructureEntry(header, ConvertEntryType(header[..header.LastIndexOf(' ')]))).ToArray();
        var fromFile = GetStructureFromFile(profile, filePath, tableName);
        if (fromFile.Count == structure.Length)
        {
            return new Structure(fromFile);
        }

        return new Structure(structure);
    }

    private static IReadOnlyList<StructureEntry> GetStructureFromFile(IExpaProfile profile, string filePath, string tableName)
    {
        var structureFolder = profile.StructureFolder;
        var structureFile = Path.Combine(structureFolder, "structure.json");
        if (!Directory.Exists(structureFolder) || !File.Exists(structureFile))
        {
            return Array.Empty<StructureEntry>();
        }

        using var rootDoc = JsonDocument.Parse(File.ReadAllText(structureFile));
        var formatFile = string.Empty;
        foreach (var property in rootDoc.RootElement.EnumerateObject())
        {
            if (Regex.IsMatch(filePath, property.Name))
            {
                formatFile = property.Value.GetString() ?? string.Empty;
                break;
            }
        }

        if (string.IsNullOrEmpty(formatFile))
        {
            return Array.Empty<StructureEntry>();
        }

        var formatPath = Path.Combine(structureFolder, formatFile);
        if (!File.Exists(formatPath))
        {
            return Array.Empty<StructureEntry>();
        }

        using var formatDoc = JsonDocument.Parse(File.ReadAllText(formatPath));
        if (!TryGetTableDefinition(formatDoc.RootElement, tableName, out var tableElement))
        {
            return Array.Empty<StructureEntry>();
        }

        return tableElement.EnumerateObject()
            .Select(static property => new StructureEntry(property.Name, ConvertEntryType(property.Value.GetString() ?? string.Empty)))
            .ToArray();
    }

    private static bool TryGetTableDefinition(JsonElement root, string tableName, out JsonElement tableElement)
    {
        if (root.TryGetProperty(tableName, out tableElement))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (Regex.IsMatch(tableName, Helpers.WrapRegex(property.Name)))
            {
                tableElement = property.Value;
                return true;
            }
        }

        tableElement = default;
        return false;
    }

    private static EntryType ConvertEntryType(string value)
    {
        return value switch
        {
            "byte" or "int8" => EntryType.Int8,
            "short" or "int16" => EntryType.Int16,
            "int" or "int32" => EntryType.Int32,
            "float" => EntryType.Float,
            "bool" => EntryType.Bool,
            "empty" => EntryType.Empty,
            "string" => EntryType.String,
            "string2" => EntryType.String2,
            "string3" => EntryType.String3,
            "int array" or "int32 array" => EntryType.Int32Array,
            "unk1" => EntryType.Unk1,
            _ => EntryType.Empty,
        };
    }

    private static string ToString(EntryType type) => type switch
    {
        EntryType.Unk1 => "unk1",
        EntryType.Int32 => "int32",
        EntryType.Int16 => "int16",
        EntryType.Int8 => "int8",
        EntryType.Float => "float",
        EntryType.String3 => "string3",
        EntryType.String => "string",
        EntryType.String2 => "string2",
        EntryType.Bool => "bool",
        EntryType.Empty => "empty",
        EntryType.Int32Array => "int32 array",
        _ => "invalid",
    };

    private sealed record CsvFile(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
    {
        public static CsvFile Read(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return new CsvFile(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
            }

            var header = ParseCsvLine(lines[0]);
            var rows = lines.Skip(1).Where(static line => line.Length > 0).Select(ParseCsvLine).ToArray();
            return new CsvFile(header, rows);
        }

        private static IReadOnlyList<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var builder = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    values.Add(builder.ToString());
                    builder.Clear();
                }
                else
                {
                    builder.Append(ch);
                }
            }

            values.Add(builder.ToString());
            return values;
        }
    }
}
