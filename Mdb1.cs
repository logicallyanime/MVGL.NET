using System.Buffers.Binary;
using System.Text;

namespace MVGLTools;

/// <summary>
/// Defines how archive data should be compressed when writing an <c>MDB1</c> archive.
/// </summary>
public enum CompressMode
{
    /// <summary>
    /// Stores files without compression.
    /// </summary>
    None,

    /// <summary>
    /// Uses the standard compression strategy for the selected profile.
    /// </summary>
    Normal,

    /// <summary>
    /// Uses the advanced compression strategy, which may deduplicate repeated data.
    /// </summary>
    Advanced,
}

/// <summary>
/// Describes the archive layout and compression behavior for an <c>MDB1</c> profile.
/// </summary>
public interface IMdbProfile
{
    /// <summary>
    /// Gets a value indicating whether this profile uses 64-bit archive entries.
    /// </summary>
    bool Use64BitEntries { get; }

    /// <summary>
    /// Gets a value indicating whether archive contents are encrypted on disk.
    /// </summary>
    bool Crypted { get; }

    /// <summary>
    /// Gets the fixed file-name field length used by the profile.
    /// </summary>
    int NameLength { get; }

    /// <summary>
    /// Gets the compressor used by the profile.
    /// </summary>
    ICompressor Compressor { get; }
}

/// <summary>
/// <c>MDB1</c> profile for encrypted DSCS archives.
/// </summary>
public sealed class DscsMdbProfile : IMdbProfile
{
    /// <inheritdoc />
    public bool Use64BitEntries => false;

    /// <inheritdoc />
    public bool Crypted => true;

    /// <inheritdoc />
    public int NameLength => 0x3C;

    /// <inheritdoc />
    public ICompressor Compressor { get; } = new DobozCompressor();
}

/// <summary>
/// <c>MDB1</c> profile for DSCS archives that are already stored without encryption.
/// </summary>
public sealed class DscsNoCryptMdbProfile : IMdbProfile
{
    /// <inheritdoc />
    public bool Use64BitEntries => false;

    /// <inheritdoc />
    public bool Crypted => false;

    /// <inheritdoc />
    public int NameLength => 0x3C;

    /// <inheritdoc />
    public ICompressor Compressor { get; } = new DobozCompressor();
}

/// <summary>
/// <c>MDB1</c> profile for DSTS archives.
/// </summary>
public sealed class DstsMdbProfile : IMdbProfile
{
    /// <inheritdoc />
    public bool Use64BitEntries => true;

    /// <inheritdoc />
    public bool Crypted => false;

    /// <inheritdoc />
    public int NameLength => 0x7C;

    /// <inheritdoc />
    public ICompressor Compressor { get; } = new Lz4Compressor();
}

/// <summary>
/// <c>MDB1</c> profile for THL archives.
/// </summary>
public sealed class ThlMdbProfile : IMdbProfile
{
    /// <inheritdoc />
    public bool Use64BitEntries => true;

    /// <inheritdoc />
    public bool Crypted => false;

    /// <inheritdoc />
    public int NameLength => 0x7C;

    /// <inheritdoc />
    public ICompressor Compressor { get; } = new Lz4Compressor();
}

/// <summary>
/// Represents an in-memory <c>MDB1</c> archive for a specific profile.
/// </summary>
/// <typeparam name="TProfile">The archive profile that controls layout, encryption, and compression behavior.</typeparam>
public sealed class Mdb1<TProfile>
    where TProfile : IMdbProfile, new()
{
    private readonly IMdbProfile _profile = new TProfile();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the path the archive was loaded from or most recently written to.
    /// </summary>
    public string? SourcePath { get; private set; }

    /// <summary>
    /// Gets the archive file paths currently stored in memory.
    /// </summary>
    public IReadOnlyCollection<string> Files => _files.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Creates a new empty archive instance.
    /// </summary>
    /// <returns>A new empty <see cref="Mdb1{TProfile}"/>.</returns>
    public static Mdb1<TProfile> Create() => new();

    /// <summary>
    /// Opens an archive from disk.
    /// </summary>
    /// <param name="path">The archive path to read.</param>
    /// <returns>The loaded archive.</returns>
    public static Mdb1<TProfile> Open(string path) => Read(path);

    /// <summary>
    /// Reads an archive from disk.
    /// </summary>
    /// <param name="path">The archive path to read.</param>
    /// <returns>The loaded archive.</returns>
    public static Mdb1<TProfile> Read(string path) => new Mdb1<TProfile>().Load(path);

    /// <summary>
    /// Loads archive contents from disk into the current instance.
    /// </summary>
    /// <param name="path">The archive path to read.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Archive path does not exist.", path);
        }

        _files.Clear();
        foreach (var file in Mdb1Format.ReadArchive(path, _profile))
        {
            _files[file.Key] = file.Value;
        }

        SourcePath = path;
        return this;
    }

    /// <summary>
    /// Adds a file from disk to the archive.
    /// </summary>
    /// <param name="sourcePath">The source file path on disk.</param>
    /// <param name="archivePath">The destination path inside the archive, or <see langword="null"/> to use the source file name.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> AddFile(string sourcePath, string? archivePath = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        var targetPath = archivePath ?? Path.GetFileName(sourcePath);
        return SetFile(targetPath, File.ReadAllBytes(sourcePath));
    }

    /// <summary>
    /// Adds or replaces a file in the archive using raw bytes.
    /// </summary>
    /// <param name="archivePath">The destination path inside the archive.</param>
    /// <param name="data">The file contents.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> AddFile(string archivePath, byte[] data) => SetFile(archivePath, data);

    /// <summary>
    /// Replaces an existing archive entry with the contents of a file on disk.
    /// </summary>
    /// <param name="sourcePath">The replacement source file path on disk.</param>
    /// <param name="archivePath">The path of the existing entry inside the archive.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> UpdateFile(string sourcePath, string archivePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        return UpdateFile(archivePath, File.ReadAllBytes(sourcePath));
    }

    /// <summary>
    /// Replaces an existing archive entry with the supplied bytes.
    /// </summary>
    /// <param name="archivePath">The path of the existing entry inside the archive.</param>
    /// <param name="data">The replacement contents.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> UpdateFile(string archivePath, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(data);

        var normalized = NormalizeArchivePath(archivePath);
        if (!_files.ContainsKey(normalized))
        {
            throw new FileNotFoundException($"File '{archivePath}' does not exist in the archive.");
        }

        _files[normalized] = data.ToArray();
        return this;
    }

    /// <summary>
    /// Recursively adds all files from a directory to the archive.
    /// </summary>
    /// <param name="sourceFolder">The source directory on disk.</param>
    /// <param name="archiveRoot">An optional root folder to prepend inside the archive.</param>
    /// <returns>The current instance.</returns>
    public Mdb1<TProfile> AddFolder(string sourceFolder, string? archiveRoot = null)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("Source folder does not exist.");
        }

        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, file).Replace(Path.DirectorySeparatorChar, '/');
            var archivePath = string.IsNullOrWhiteSpace(archiveRoot)
                ? relativePath
                : Path.Combine(archiveRoot, relativePath).Replace(Path.DirectorySeparatorChar, '/');
            SetFile(archivePath, File.ReadAllBytes(file));
        }

        return this;
    }

    /// <summary>
    /// Removes a file from the archive.
    /// </summary>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    /// <returns><see langword="true"/> if the file was removed; otherwise, <see langword="false"/>.</returns>
    public bool RemoveFile(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return _files.Remove(NormalizeArchivePath(archivePath));
    }

    /// <summary>
    /// Removes a file from the archive.
    /// </summary>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    /// <returns><see langword="true"/> if the file was removed; otherwise, <see langword="false"/>.</returns>
    public bool DeleteFile(string archivePath) => RemoveFile(archivePath);

    /// <summary>
    /// Determines whether the archive contains a file.
    /// </summary>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    /// <returns><see langword="true"/> if the file exists; otherwise, <see langword="false"/>.</returns>
    public bool ContainsFile(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return _files.ContainsKey(NormalizeArchivePath(archivePath));
    }

    /// <summary>
    /// Gets a copy of the data for a file stored in the archive.
    /// </summary>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    /// <returns>A new byte array containing the file data.</returns>
    public byte[] GetFileData(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return _files[NormalizeArchivePath(archivePath)].ToArray();
    }

    /// <summary>
    /// Gets a copy of the data for a file stored in the archive.
    /// </summary>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    /// <returns>A new byte array containing the file data.</returns>
    public byte[] ReadFileData(string archivePath) => GetFileData(archivePath);

    /// <summary>
    /// Extracts every file in the archive to a directory on disk.
    /// </summary>
    /// <param name="outputFolder">The destination directory.</param>
    public void Extract(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);

        var files = _files.OrderBy(static file => file.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var totalFiles = files.Length;
        var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var processedFiles = 0;

        Helpers.Log($"[Extract] Extracting {totalFiles} files with {workerCount} workers...");

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
        };

        Parallel.ForEach(files, options, file =>
        {
            var outputPath = Path.Combine(outputFolder, file.Key.Replace('/', Path.DirectorySeparatorChar));
            WriteExtractedFile(outputPath, file.Value);

            var current = Interlocked.Increment(ref processedFiles);
            if (current == totalFiles || current % 50 == 0)
            {
                Helpers.Log($"[Extract] {current}/{totalFiles} files");
            }
        });

        Helpers.Log("[Extract] Done.");
    }

    /// <summary>
    /// Extracts a single archive entry to a file on disk.
    /// </summary>
    /// <param name="outputPath">The destination file path.</param>
    /// <param name="archivePath">The path of the entry inside the archive.</param>
    public void ExtractSingleFile(string outputPath, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        WriteExtractedFile(outputPath, GetFileData(archivePath));
    }

    /// <summary>
    /// Builds the archive into a memory stream.
    /// </summary>
    /// <param name="compress">The compression mode to use.</param>
    /// <returns>A <see cref="MemoryStream"/> positioned at the beginning of the written archive.</returns>
    public MemoryStream ToStream(CompressMode compress = CompressMode.Normal)
    {
        var output = new MemoryStream();
        Write(output, compress);
        output.Position = 0;
        return output;
    }

    /// <summary>
    /// Writes the archive to a seekable output stream.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="compress">The compression mode to use.</param>
    public void Write(Stream output, CompressMode compress = CompressMode.Normal)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        if (!output.CanSeek)
        {
            throw new ArgumentException("Output stream must be seekable.", nameof(output));
        }

        Mdb1Format.WriteArchive(output, _profile, _files, compress);
    }

    /// <summary>
    /// Writes the archive to disk.
    /// </summary>
    /// <param name="target">The destination archive path.</param>
    /// <param name="compress">The compression mode to use.</param>
    public void Write(string target, CompressMode compress = CompressMode.Normal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(output, compress);
        SourcePath = target;
    }

    private Mdb1<TProfile> SetFile(string archivePath, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(data);

        _files[NormalizeArchivePath(archivePath)] = data.ToArray();
        return this;
    }

    private static void WriteExtractedFile(string outputPath, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputPath, data);
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
}

internal static class Mdb1Format
{
    internal const uint Mdb1MagicValue = 0x3142444D;
    private const int ExtensionLength = 4;
    private const ulong Invalid = ulong.MaxValue;

    internal readonly record struct Header(ulong FileEntryCount, ulong FileNameCount, ulong DataEntryCount, ulong DataStart, ulong TotalSize, uint MagicValue);
    internal readonly record struct TreeEntry(ulong CompareBit, ulong DataId, ulong Left, ulong Right);
    internal readonly record struct DataEntry(ulong Offset, ulong FullSize, ulong CompressedSize);
    private readonly record struct TreeName(string Name, string ArchivePath);
    private readonly record struct TreeNode(ulong CompareBit, ulong Left, ulong Right, TreeName Name);
    private readonly record struct CompressionResult(ulong OriginalSize, uint Crc, byte[] Data);
    private readonly record struct ArchiveSource(string ArchivePath, byte[] Data);
    private sealed record QueueEntry(ulong ParentNode, ulong CompareBit, List<TreeName> List, List<TreeName> NodeList, bool IsLeft);

    internal static void CryptArray(byte[] array, long offset)
    {
        for (var i = 0; i < array.Length; i++)
        {
            array[i] ^= (byte)(CryptKey1[(offset + i) % CryptKey1.Length] ^ CryptKey2[(offset + i) % CryptKey2.Length]);
        }
    }

    internal static Dictionary<string, byte[]> ReadArchive(string path, IMdbProfile profile)
    {
        Helpers.Log($"[MDB1] Opening {Path.GetFileName(path)}...");

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.SequentialScan);

        var header = ReadHeader(input, profile);
        if (header.MagicValue != Mdb1MagicValue)
        {
            throw new InvalidDataException("Given file is not an MVGL archive.");
        }

        Helpers.Log($"[MDB1] Reading archive tables: tree={header.FileEntryCount}, names={header.FileNameCount}, data={header.DataEntryCount}");

        var treeEntries = new List<TreeEntry>(checked((int)header.FileEntryCount));
        var nameEntries = new List<string>(checked((int)header.FileNameCount));
        var dataEntries = new List<DataEntry>(checked((int)header.DataEntryCount));

        for (ulong i = 0; i < header.FileEntryCount; i++)
        {
            treeEntries.Add(ReadTreeEntry(input, profile));
            if ((i + 1) % 5000 == 0 || i + 1 == header.FileEntryCount)
            {
                Helpers.Log($"[MDB1] Tree entries: {i + 1}/{header.FileEntryCount}");
            }
        }

        for (ulong i = 0; i < header.FileNameCount; i++)
        {
            nameEntries.Add(ReadNameEntry(input, profile));
            if ((i + 1) % 5000 == 0 || i + 1 == header.FileNameCount)
            {
                Helpers.Log($"[MDB1] Name entries: {i + 1}/{header.FileNameCount}");
            }
        }

        for (ulong i = 0; i < header.DataEntryCount; i++)
        {
            dataEntries.Add(ReadDataEntry(input, profile));
            if ((i + 1) % 5000 == 0 || i + 1 == header.DataEntryCount)
            {
                Helpers.Log($"[MDB1] Data entries: {i + 1}/{header.DataEntryCount}");
            }
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var dataCache = new Dictionary<int, byte[]>();

        for (var i = 0; i < treeEntries.Count; i++)
        {
            var dataId = treeEntries[i].DataId;
            if (IsInvalidTreeIndex(profile, dataId))
            {
                continue;
            }

            var archivePath = NormalizeArchivePath(nameEntries[i]);
            if (string.IsNullOrEmpty(archivePath))
            {
                continue;
            }

            var dataIndex = checked((int)dataId);
            if (!dataCache.TryGetValue(dataIndex, out var data))
            {
                data = ReadFile(input, header.DataStart, dataEntries[dataIndex], profile);
                dataCache[dataIndex] = data;
            }

            files[archivePath] = data.ToArray();
        }

        Helpers.Log($"[MDB1] Indexed {files.Count} files.");
        return files;
    }

    internal static void WriteArchive(Stream output, IMdbProfile profile, IReadOnlyDictionary<string, byte[]> files, CompressMode compress)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(files);

        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        if (!output.CanSeek)
        {
            throw new ArgumentException("Output stream must be seekable.", nameof(output));
        }

        var orderedFiles = files
            .Select(static file => new ArchiveSource(NormalizeArchivePath(file.Key), file.Value.ToArray()))
            .ToArray();

        Helpers.Log("[Pack] Generating File Tree...");
        var tree = GenerateTree(orderedFiles, profile);

        var tasks = new Dictionary<string, Task<CompressionResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in orderedFiles)
        {
            var archivePath = NormalizeArchivePath(file.ArchivePath);
            if (!tasks.ContainsKey(archivePath))
            {
                tasks[archivePath] = Task.Run(() => CompressFileData(file.Data, profile.Compressor, compress));
            }
        }

        var fileCount = orderedFiles.Length;
        var headerSize = profile.Use64BitEntries ? 0x20 : 0x14;
        var treeEntrySize = (profile.Use64BitEntries ? 0x10 : 0x08) * (fileCount + 1);
        var nameEntrySize = (ExtensionLength + profile.NameLength) * (fileCount + 1);
        var dataEntrySize = (profile.Use64BitEntries ? 0x18 : 0x0C) * fileCount;
        var dataStart = (ulong)(headerSize + treeEntrySize + nameEntrySize + dataEntrySize);

        var treeEntries = new List<TreeEntry>(fileCount + 1)
        {
            new TreeEntry(Invalid, Invalid, 0, fileCount == 0 ? 0UL : 1UL),
        };
        var nameEntries = new List<string>(fileCount + 1)
        {
            string.Empty,
        };
        var dataEntries = new List<DataEntry>(fileCount);
        var dataMap = new Dictionary<uint, int>();
        var fileDataIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var baseOffset = output.Position;
        ulong offset = 0;
        var fileId = 0;

        output.SetLength(baseOffset);
        foreach (var file in orderedFiles)
        {
            fileId++;
            if (fileId % 200 == 0 || fileId == fileCount)
            {
                Helpers.Log($"[Pack] Writing File {fileId} of {fileCount}");
            }

            var archivePath = NormalizeArchivePath(file.ArchivePath);
            var data = tasks[archivePath].GetAwaiter().GetResult();
            var existing = compress == CompressMode.Advanced && dataMap.TryGetValue(data.Crc, out var existingId)
                ? existingId
                : -1;
            var dataId = existing >= 0 ? existing : dataEntries.Count;
            fileDataIds[archivePath] = dataId;

            if (existing < 0)
            {
                dataMap[data.Crc] = dataId;
                dataEntries.Add(new DataEntry(offset, data.OriginalSize, (ulong)data.Data.Length));
                output.Seek(checked((long)(baseOffset + (long)dataStart + (long)offset)), SeekOrigin.Begin);
                Helpers.WriteBytes(output, data.Data, profile.Crypted);
                offset += (ulong)data.Data.Length;
            }
        }

        foreach (var node in tree)
        {
            if (node.CompareBit == Invalid)
            {
                continue;
            }

            var archivePath = NormalizeArchivePath(node.Name.ArchivePath);
            treeEntries.Add(new TreeEntry(node.CompareBit, (ulong)fileDataIds[archivePath], node.Left, node.Right));
            nameEntries.Add(node.Name.Name);
        }

        output.Seek(baseOffset, SeekOrigin.Begin);
        WriteHeader(output, profile, new Header((ulong)treeEntries.Count, (ulong)nameEntries.Count, (ulong)dataEntries.Count, dataStart, dataStart + offset, Mdb1MagicValue));
        foreach (var treeEntry in treeEntries)
        {
            WriteTreeEntry(output, profile, treeEntry);
        }

        foreach (var nameEntry in nameEntries)
        {
            WriteNameEntry(output, profile, nameEntry);
        }

        foreach (var dataEntry in dataEntries)
        {
            WriteDataEntry(output, profile, dataEntry);
        }

        output.Seek(checked((long)(baseOffset + (long)(dataStart + offset))), SeekOrigin.Begin);
    }

    private static byte[] ReadFile(FileStream input, ulong dataStart, DataEntry entry, IMdbProfile profile)
    {
        input.Seek((long)(dataStart + entry.Offset), SeekOrigin.Begin);
        var inputData = Helpers.ReadBytes(input, checked((int)entry.CompressedSize), profile.Crypted);
        return profile.Compressor.Decompress(inputData, checked((int)entry.FullSize));
    }

    private static CompressionResult CompressFileData(byte[] data, ICompressor compressor, CompressMode mode)
    {
        var rawData = data.ToArray();
        var checksum = mode == CompressMode.Advanced ? Helpers.GetChecksum(rawData) : 0;
        if (rawData.Length == 0 || mode == CompressMode.None)
        {
            return new CompressionResult((ulong)rawData.Length, checksum, rawData);
        }

        var compressed = compressor.Compress(rawData);
        if (compressed.Length + 4 >= rawData.Length)
        {
            compressed = rawData;
        }

        return new CompressionResult((ulong)rawData.Length, checksum, compressed);
    }

    private static List<TreeNode> GenerateTree(IReadOnlyList<ArchiveSource> files, IMdbProfile profile)
    {
        if (files.Count == 0)
        {
            return [new TreeNode(Invalid, 0, 0, default)];
        }

        var fileNames = files
            .Select(file => new TreeName(BuildMdb1Path(file.ArchivePath, profile), file.ArchivePath))
            .ToList();

        var nodes = new List<TreeNode> { new(Invalid, 0, 0, default) };
        var queue = new LinkedList<QueueEntry>();
        queue.AddFirst(new QueueEntry(0, Invalid, fileNames, new List<TreeName>(), false));

        while (queue.Count > 0)
        {
            var entry = queue.First!.Value;
            queue.RemoveFirst();
            var parent = nodes[(int)entry.ParentNode];
            var nodeless = entry.List.Where(file => entry.NodeList.All(node => node.Name != file.Name)).ToList();
            var withNode = entry.List.Where(file => entry.NodeList.Any(node => node.Name == file.Name)).ToList();

            if (nodeless.Count == 0)
            {
                var firstFile = entry.List[0];
                var offset = nodes.FindIndex(node => node.Name.Name == firstFile.Name);
                if (entry.IsLeft)
                {
                    parent = parent with { Left = (ulong)offset };
                }
                else
                {
                    parent = parent with { Right = (ulong)offset };
                }

                nodes[(int)entry.ParentNode] = parent;
                continue;
            }

            var nextCompareBit = entry.CompareBit == Invalid ? 0 : entry.CompareBit + 1;
            var child = FindFirstBitMismatch(nextCompareBit, nodeless, withNode);
            var childIndex = (ulong)nodes.Count;
            if (entry.IsLeft)
            {
                parent = parent with { Left = childIndex };
            }
            else
            {
                parent = parent with { Right = childIndex };
            }

            nodes[(int)entry.ParentNode] = parent;

            var left = new List<TreeName>();
            var right = new List<TreeName>();
            foreach (var file in entry.List)
            {
                if (IsBitSet(file.Name, child.CompareBit))
                {
                    right.Add(file);
                }
                else
                {
                    left.Add(file);
                }
            }

            var newNodeList = new List<TreeName>(entry.NodeList) { child.Name };
            nodes.Add(child);
            if (left.Count > 0)
            {
                queue.AddFirst(new QueueEntry(childIndex, child.CompareBit, left, newNodeList, true));
            }

            if (right.Count > 0)
            {
                queue.AddFirst(new QueueEntry(childIndex, child.CompareBit, right, newNodeList, false));
            }
        }

        return nodes;
    }

    private static TreeNode FindFirstBitMismatch(ulong first, IReadOnlyList<TreeName> nodeless, IReadOnlyList<TreeName> withNode)
    {
        if (withNode.Count == 0)
        {
            return new TreeNode(first, 0, 0, nodeless[0]);
        }

        for (ulong i = first; i < 1024; i++)
        {
            var set = false;
            var unset = false;
            foreach (var file in withNode)
            {
                if (IsBitSet(file.Name, i))
                {
                    set = true;
                }
                else
                {
                    unset = true;
                }

                if (set && unset)
                {
                    return new TreeNode(i, 0, 0, nodeless[0]);
                }
            }

            var mismatch = nodeless.FirstOrDefault(file =>
            {
                var value = IsBitSet(file.Name, i);
                return (value && unset) || (!value && set);
            });

            if (!string.IsNullOrEmpty(mismatch.Name))
            {
                return new TreeNode(i, 0, 0, mismatch);
            }
        }

        return new TreeNode(Invalid, Invalid, 0, default);
    }

    private static bool IsBitSet(string name, ulong position)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        var byteIndex = (int)(position >> 3);
        var bit = (int)(position & 7);
        if (bytes.Length <= byteIndex)
        {
            return false;
        }

        return ((bytes[byteIndex] >> bit) & 1) != 0;
    }

    private static string BuildMdb1Path(string archivePath, IMdbProfile profile)
    {
        var extension = Path.GetExtension(archivePath).TrimStart('.');
        if (extension.Length == 3)
        {
            extension += " ";
        }

        extension = extension.Length > 4 ? extension[..4] : extension;
        var fileName = Path.ChangeExtension(archivePath, null)!.Replace('/', '\\');
        var extensionPart = extension.PadRight(4, '\0');
        var namePart = fileName.Length > profile.NameLength ? fileName[..profile.NameLength] : fileName;
        return extensionPart + namePart;
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

    internal static Header ReadHeader(FileStream stream, IMdbProfile profile)
    {
        if (profile.Use64BitEntries)
        {
            var magicValue = Helpers.ReadUInt32(stream, profile.Crypted);
            var fileEntryCount = Helpers.ReadUInt32(stream, profile.Crypted);
            var fileNameCount = Helpers.ReadUInt32(stream, profile.Crypted);
            var dataEntryCount = Helpers.ReadUInt32(stream, profile.Crypted);
            var dataStart = Helpers.ReadUInt64(stream, profile.Crypted);
            var totalSize = Helpers.ReadUInt64(stream, profile.Crypted);
            return new Header(fileEntryCount, fileNameCount, dataEntryCount, dataStart, totalSize, magicValue);
        }

        var magic = Helpers.ReadUInt32(stream, profile.Crypted);
        var fileEntries = Helpers.ReadUInt16(stream, profile.Crypted);
        var fileNames = Helpers.ReadUInt16(stream, profile.Crypted);
        var dataEntries32 = Helpers.ReadUInt32(stream, profile.Crypted);
        var dataStart32 = Helpers.ReadUInt32(stream, profile.Crypted);
        var totalSize32 = Helpers.ReadUInt32(stream, profile.Crypted);
        return new Header(fileEntries, fileNames, dataEntries32, dataStart32, totalSize32, magic);
    }

    internal static bool IsInvalidTreeIndex(IMdbProfile profile, ulong value)
    {
        if (profile.Use64BitEntries)
        {
            return value == uint.MaxValue;
        }

        return value == ushort.MaxValue;
    }

    private static ulong NormalizeTreeValueForWrite(IMdbProfile profile, ulong value)
    {
        if (value != Invalid)
        {
            return value;
        }

        return profile.Use64BitEntries ? uint.MaxValue : ushort.MaxValue;
    }

    private static void WriteHeader(Stream stream, IMdbProfile profile, Header header)
    {
        if (profile.Use64BitEntries)
        {
            Helpers.WriteUInt32(stream, header.MagicValue, profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)header.FileEntryCount), profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)header.FileNameCount), profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)header.DataEntryCount), profile.Crypted);
            Helpers.WriteUInt64(stream, header.DataStart, profile.Crypted);
            Helpers.WriteUInt64(stream, header.TotalSize, profile.Crypted);
            return;
        }

        Helpers.WriteUInt32(stream, header.MagicValue, profile.Crypted);
        Helpers.WriteUInt16(stream, checked((ushort)header.FileEntryCount), profile.Crypted);
        Helpers.WriteUInt16(stream, checked((ushort)header.FileNameCount), profile.Crypted);
        Helpers.WriteUInt32(stream, checked((uint)header.DataEntryCount), profile.Crypted);
        Helpers.WriteUInt32(stream, checked((uint)header.DataStart), profile.Crypted);
        Helpers.WriteUInt32(stream, checked((uint)header.TotalSize), profile.Crypted);
    }

    internal static TreeEntry ReadTreeEntry(FileStream stream, IMdbProfile profile)
    {
        if (profile.Use64BitEntries)
        {
            return new TreeEntry(Helpers.ReadUInt32(stream, profile.Crypted), Helpers.ReadUInt32(stream, profile.Crypted), Helpers.ReadUInt32(stream, profile.Crypted), Helpers.ReadUInt32(stream, profile.Crypted));
        }

        return new TreeEntry(Helpers.ReadUInt16(stream, profile.Crypted), Helpers.ReadUInt16(stream, profile.Crypted), Helpers.ReadUInt16(stream, profile.Crypted), Helpers.ReadUInt16(stream, profile.Crypted));
    }

    private static void WriteTreeEntry(Stream stream, IMdbProfile profile, TreeEntry entry)
    {
        if (profile.Use64BitEntries)
        {
            Helpers.WriteUInt32(stream, checked((uint)NormalizeTreeValueForWrite(profile, entry.CompareBit)), profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)NormalizeTreeValueForWrite(profile, entry.DataId)), profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)NormalizeTreeValueForWrite(profile, entry.Left)), profile.Crypted);
            Helpers.WriteUInt32(stream, checked((uint)NormalizeTreeValueForWrite(profile, entry.Right)), profile.Crypted);
            return;
        }

        Helpers.WriteUInt16(stream, checked((ushort)NormalizeTreeValueForWrite(profile, entry.CompareBit)), profile.Crypted);
        Helpers.WriteUInt16(stream, checked((ushort)NormalizeTreeValueForWrite(profile, entry.DataId)), profile.Crypted);
        Helpers.WriteUInt16(stream, checked((ushort)NormalizeTreeValueForWrite(profile, entry.Left)), profile.Crypted);
        Helpers.WriteUInt16(stream, checked((ushort)NormalizeTreeValueForWrite(profile, entry.Right)), profile.Crypted);
    }

    internal static string ReadNameEntry(FileStream stream, IMdbProfile profile)
    {
        var bytes = Helpers.ReadBytes(stream, ExtensionLength + profile.NameLength, profile.Crypted);
        var extension = Helpers.TrimFixedString(bytes.AsSpan(0, ExtensionLength));
        var name = Helpers.TrimFixedString(bytes.AsSpan(ExtensionLength, profile.NameLength));
        return string.IsNullOrEmpty(name) && string.IsNullOrEmpty(extension) ? string.Empty : $"{name}.{extension}";
    }

    private static void WriteNameEntry(Stream stream, IMdbProfile profile, string entry)
    {
        var buffer = new byte[ExtensionLength + profile.NameLength];
        if (!string.IsNullOrEmpty(entry))
        {
            var normalized = entry;
            var extensionBytes = Encoding.ASCII.GetBytes(normalized.Length >= 4 ? normalized[..4] : normalized.PadRight(4));
            Array.Copy(extensionBytes, 0, buffer, 0, Math.Min(extensionBytes.Length, 4));
            var nameBytes = Encoding.ASCII.GetBytes(normalized.Length > 4 ? normalized[4..] : string.Empty);
            Array.Copy(nameBytes, 0, buffer, 4, Math.Min(nameBytes.Length, profile.NameLength));
        }

        Helpers.WriteBytes(stream, buffer, profile.Crypted);
    }

    internal static DataEntry ReadDataEntry(FileStream stream, IMdbProfile profile)
    {
        if (profile.Use64BitEntries)
        {
            return new DataEntry(Helpers.ReadUInt64(stream, profile.Crypted), Helpers.ReadUInt64(stream, profile.Crypted), Helpers.ReadUInt64(stream, profile.Crypted));
        }

        return new DataEntry(Helpers.ReadUInt32(stream, profile.Crypted), Helpers.ReadUInt32(stream, profile.Crypted), Helpers.ReadUInt32(stream, profile.Crypted));
    }

    private static void WriteDataEntry(Stream stream, IMdbProfile profile, DataEntry entry)
    {
        if (profile.Use64BitEntries)
        {
            Helpers.WriteUInt64(stream, entry.Offset, profile.Crypted);
            Helpers.WriteUInt64(stream, entry.FullSize, profile.Crypted);
            Helpers.WriteUInt64(stream, entry.CompressedSize, profile.Crypted);
            return;
        }

        Helpers.WriteUInt32(stream, checked((uint)entry.Offset), profile.Crypted);
        Helpers.WriteUInt32(stream, checked((uint)entry.FullSize), profile.Crypted);
        Helpers.WriteUInt32(stream, checked((uint)entry.CompressedSize), profile.Crypted);
    }

    private static readonly byte[] CryptKey1 = [0xD3, 0x53, 0xD2, 0x85, 0xDC, 0x87, 0x77, 0xA7, 0x16, 0xFA, 0x8D, 0x45, 0x9D, 0x14, 0x60, 0x3B, 0x9B, 0x7B, 0xDA, 0xED, 0x25, 0xFD, 0xF5, 0x8D, 0x44, 0xD0, 0xEB, 0x8B, 0xAB, 0x4B, 0x6A, 0x3E, 0x01, 0x28, 0x63, 0xA3, 0xE3, 0x23, 0x63, 0xA3, 0xE2, 0x55, 0x6D, 0xA5, 0x7C, 0xA8, 0xE4, 0xF0, 0x8B, 0xAA, 0x7D, 0x74, 0x40, 0x9C, 0x47, 0x36, 0x9A, 0xAE, 0xB1, 0x19, 0x60, 0x3B, 0x9A, 0xAD, 0xE4, 0xEF, 0xBE, 0x82, 0x76, 0xDA, 0xED, 0x25, 0xFD, 0xF5, 0x8D, 0x45, 0x9C, 0x47, 0x37, 0x67, 0xD6, 0xB9, 0x81, 0xA8, 0xE3, 0x22, 0x96, 0x79, 0x40, 0x9C, 0x48, 0x04, 0x90, 0xAB, 0x4B, 0x6B, 0x0A, 0x5E, 0xA1, 0x48, 0x03, 0xC3, 0x83, 0x42, 0x35, 0xCD, 0x85, 0xDD, 0x55, 0x6C, 0xD7, 0x87, 0x76, 0xD9, 0x20, 0xFC, 0x28, 0x63, 0xA2, 0x15, 0x2D, 0x64, 0x6F, 0x3E, 0x02, 0xF6, 0x5A, 0x6D, 0xA5, 0x7D, 0x74, 0x3F, 0xCE, 0x51, 0x39, 0x00, 0x5C, 0x08, 0xC3, 0x82, 0x75, 0x0C, 0xF8, 0xF3, 0xF2, 0x26, 0xCA, 0x1E, 0x62, 0xD5, 0xED, 0x24, 0x30, 0xCC, 0xB8, 0xB3, 0xB3, 0xB2, 0xE6, 0x89, 0x11, 0xF9, 0xC0, 0x1B, 0xFA, 0x8E, 0x12, 0xC6, 0xE9, 0xF1, 0x58, 0xD4, 0x20, 0xFB, 0x5B, 0x3B, 0x9A, 0xAD, 0xE4, 0xF0, 0x8B, 0xAA, 0x7D, 0x74, 0x40, 0x9C, 0x47, 0x36, 0x9A, 0xAD, 0xE4, 0xF0, 0x8C, 0x77, 0xA7, 0x16, 0xFA, 0x8D, 0x45, 0x9C, 0x47, 0x36, 0x99, 0xE0, 0xBB, 0x1B, 0xFB, 0x5B, 0x3B, 0x9B, 0x7A, 0x0E, 0x91, 0x78, 0x73, 0x73, 0x72, 0xA5, 0x7D, 0x75, 0x0C, 0xF7, 0x26, 0xC9, 0x51, 0x38, 0x34, 0x00, 0x5C, 0x08, 0xC4, 0x50, 0x6C, 0xD7, 0x86, 0xAA, 0x7D, 0x75, 0x0C, 0xF7, 0x26, 0xC9, 0x50, 0x6B, 0x0B, 0x2A, 0xFE, 0xC2, 0xB6, 0x19, 0x60, 0x3C, 0x68, 0xA4, 0xB0, 0x4C, 0x38, 0x33, 0x32, 0x65, 0x3D, 0x34, 0x00, 0x5C, 0x07, 0xF6, 0x59, 0xA0, 0x7C, 0xA7, 0x16, 0xF9, 0xC1, 0xE8, 0x24, 0x2F, 0xFF, 0x8E, 0x12, 0xC6, 0xE9, 0xF0, 0x8C, 0x78, 0x74, 0x40, 0x9B, 0x7A, 0x0E, 0x91, 0x79, 0x41, 0x69, 0x71, 0xD9, 0x20, 0xFB, 0x5B, 0x3B, 0x9A, 0xAE, 0xB2, 0xE6, 0x8A, 0xDE, 0x22, 0x95, 0xAC, 0x18, 0x93, 0x12, 0xC5, 0x1D, 0x95, 0xAC, 0x18, 0x93, 0x13, 0x93, 0x12, 0xC6, 0xEA, 0xBD, 0xB5, 0x4C, 0x38, 0x34, 0x00, 0x5B, 0x3B, 0x9A, 0xAD, 0xE5, 0xBD, 0xB5, 0x4C, 0x38, 0x34, 0xFF, 0x8E, 0x11, 0xF8, 0xF4, 0xC0, 0x1B, 0xFB, 0x5B, 0x3B, 0x9A, 0xAE, 0xB2, 0xE5, 0xBD, 0xB5, 0x4D, 0x05, 0x5D, 0xD5, 0xED, 0x24, 0x30, 0xCC, 0xB8, 0xB4, 0x7F, 0x0F, 0x5E, 0xA2, 0x15, 0x2D, 0x64, 0x6F, 0x3E, 0x02, 0xF6, 0x59, 0xA1, 0x48, 0x03, 0xC2, 0xB6, 0x1A, 0x2E, 0x31, 0x98, 0x13, 0x93, 0x12, 0xC5, 0x1D, 0x95, 0xAD, 0xE4, 0xF0, 0x8C, 0x77, 0xA7, 0x16, 0xF9, 0xC1, 0xE9, 0xF1, 0x58, 0xD4, 0x20, 0xFB, 0x5B, 0x3A, 0xCD, 0x84, 0x10, 0x2C, 0x98, 0x14, 0x5F, 0x6E, 0x72, 0xA5, 0x7C, 0xA8, 0xE4, 0xEF, 0xBE, 0x81, 0xA9, 0xB0, 0x4B, 0x6B, 0x0A, 0x5D, 0xD4, 0x20, 0xFC, 0x27, 0x97, 0x47, 0x37, 0x66, 0x09, 0x90, 0xAB, 0x4A, 0x9E, 0xE2, 0x55, 0x6C, 0xD8, 0x54, 0x9F, 0xAE, 0xB2, 0xE6, 0x89, 0x11, 0xF9, 0xC0, 0x1C, 0xC7, 0xB6, 0x1A, 0x2E, 0x32, 0x66, 0x09, 0x91, 0x79, 0x41, 0x68, 0xA4, 0xB0, 0x4B, 0x6A, 0x3E, 0x02, 0xF6, 0x59, 0xA1, 0x48, 0x04, 0x90, 0xAB, 0x4B, 0x6A, 0x3E, 0x01, 0x28, 0x63, 0xA3, 0xE2, 0x56, 0x39, 0x01, 0x28, 0x63, 0xA2, 0x16, 0xF9, 0xC0, 0x1B, 0xFA, 0x8E, 0x11, 0xF9, 0xC1, 0xE9, 0xF1, 0x59, 0xA1, 0x48, 0x03, 0xC3, 0x82, 0x76, 0xD9, 0x20, 0xFC, 0x27, 0x96, 0x79, 0x40, 0x9B, 0x7B, 0xDA, 0xEE, 0xF1, 0x59, 0xA0, 0x7C, 0xA7, 0x17, 0xC7, 0xB7, 0xE6, 0x89, 0x11, 0xF9, 0xC1, 0xE9, 0xF1, 0x59, 0xA0, 0x7C, 0xA7, 0x16, 0xFA, 0x8D, 0x44, 0xCF, 0x1E, 0x62, 0xD5, 0xED, 0x25, 0xFD, 0xF4, 0xBF, 0x4E, 0xD1, 0xB8, 0xB3, 0xB2, 0xE5, 0xBC, 0xE7, 0x57, 0x06, 0x2A, 0xFE, 0xC2, 0xB5, 0x4D, 0x04, 0x8F, 0xDE, 0x22, 0x96, 0x79, 0x40, 0x9B, 0x7B, 0xDA, 0xED, 0x25, 0xFC, 0x28, 0x64, 0x70, 0x0C, 0xF7, 0x27, 0x97, 0x46, 0x6A, 0x3D, 0x35, 0xCC, 0xB7, 0xE7, 0x56, 0x3A, 0xCD, 0x84, 0x0F, 0x5E, 0xA1, 0x48, 0x04, 0x90, 0xAC, 0x18, 0x94, 0xDF, 0xEE, 0xF1, 0x59, 0xA1, 0x49, 0xD1, 0xB9, 0x80, 0xDC, 0x88, 0x43, 0x03, 0xC3, 0x82, 0x76, 0xD9, 0x20, 0xFB, 0x5B, 0x3A, 0xCE, 0x52, 0x06, 0x29, 0x31, 0x98, 0x14, 0x60, 0x3C, 0x67, 0xD7, 0x86, 0xAA, 0x7E, 0x42, 0x35, 0xCD, 0x85, 0xDD, 0x55, 0x6D, 0xA5, 0x7D, 0x75, 0x0D, 0xC5, 0x1D, 0x94, 0xE0, 0xBB, 0x1A, 0x2D, 0x64, 0x6F, 0x3E, 0x01, 0x29, 0x30, 0xCB, 0xEA, 0xBE, 0x81, 0xA9, 0xB0, 0x4C, 0x38, 0x34, 0xFF, 0x8F, 0xDE, 0x22, 0x95, 0xAD, 0xE5, 0xBD, 0xB5, 0x4C, 0x37, 0x66, 0x09, 0x91, 0x79, 0x40, 0x9C, 0x47, 0x37, 0x67, 0xD7, 0x86, 0xAA, 0x7D, 0x74, 0x40, 0x9C, 0x47, 0x37, 0x66, 0x09, 0x90, 0xAB, 0x4B, 0x6B, 0x0A, 0x5D, 0xD5, 0xEC, 0x58, 0xD3, 0x53, 0xD3, 0x53, 0xD3, 0x52, 0x06, 0x29, 0x30, 0xCC, 0xB8, 0xB4, 0x7F, 0x0F, 0x5F, 0x6F, 0x3E, 0x02, 0xF5, 0x8D, 0x45, 0x9D, 0x14, 0x5F, 0x6F, 0x3E, 0x01, 0x29, 0x31, 0x98, 0x13, 0x93, 0x13, 0x92, 0x45, 0x9D, 0x14, 0x5F, 0x6E, 0x71, 0xD8, 0x54, 0xA0, 0x7B, 0xDB, 0xBA, 0x4D, 0x05, 0x5C, 0x08, 0xC3, 0x82, 0x75, 0x0D, 0xC4, 0x4F, 0x9F, 0xAE, 0xB1, 0x19, 0x60, 0x3C, 0x68, 0xA4, 0xAF, 0x7F, 0x0E, 0x92, 0x45, 0x9D, 0x14, 0x60, 0x3C, 0x67, 0xD7, 0x86, 0xA9, 0xB0, 0x4C, 0x37, 0x67, 0xD6, 0xBA, 0x4D, 0x04, 0x90, 0xAB, 0x4A, 0x9D, 0x14, 0x5F, 0x6E, 0x72, 0xA6, 0x49, 0xD1, 0xB9, 0x80, 0xDB, 0xBB, 0x1B, 0xFA, 0x8D, 0x44, 0xCF, 0x1E, 0x62, 0xD6, 0xB9, 0x80, 0xDC, 0x87, 0x77, 0xA6, 0x49, 0xD1, 0xB9, 0x80, 0xDB, 0xBB, 0x1B, 0xFA, 0x8D, 0x44, 0xD0, 0xEB, 0x8A, 0xDE, 0x21, 0xC8, 0x84, 0x0F, 0x5E, 0xA1, 0x49, 0xD1, 0xB8, 0xB4, 0x80, 0xDC, 0x88, 0x43, 0x03, 0xC3, 0x83, 0x42, 0x35, 0xCD, 0x84, 0x0F, 0x5E, 0xA1, 0x48, 0x04, 0x8F, 0xDF, 0xEE, 0xF1, 0x59, 0xA0, 0x7C, 0xA7, 0x17, 0xC7, 0xB6, 0x19, 0x61, 0x08, 0xC4, 0x4F, 0x9F, 0xAE, 0xB1, 0x18, 0x93, 0x12, 0xC6, 0xEA, 0xBD, 0xB4, 0x80, 0xDC, 0x88, 0x44, 0xD0, 0xEB, 0x8B, 0xAB, 0x4B, 0x6B, 0x0B, 0x2A, 0xFE, 0xC2, 0xB6, 0x1A, 0x2D, 0x65, 0x3D, 0x35, 0xCC, 0xB8, 0xB4, 0x80, 0xDC, 0x88, 0x43, 0x03, 0xC2, 0xB5, 0x4D, 0x04, 0x8F, 0xDF, 0xEF, 0xBE, 0x81, 0xA8, 0xE3, 0x23, 0x63, 0xA2, 0x16, 0xF9, 0xC0, 0x1B, 0xFA, 0x8E, 0x11, 0xF9, 0xC1, 0xE9, 0xF0, 0x8B, 0xAA, 0x7E, 0x42, 0x35, 0xCD, 0x84, 0x10, 0x2C, 0x97, 0x46, 0x69, 0x70, 0x0C, 0xF7, 0x27, 0x97, 0x47, 0x37, 0x66, 0x0A, 0x5E, 0xA1, 0x49, 0xD0, 0xEC, 0x58, 0xD4, 0x20, 0xFC, 0x28, 0x64, 0x6F, 0x3E, 0x01, 0x28, 0x63, 0xA2, 0x15, 0x2C, 0x98, 0x14, 0x60, 0x3B, 0x9B];
    private static readonly byte[] CryptKey2 = [0x92, 0x85, 0x1D, 0xD4, 0x60, 0x7B, 0x1B, 0x3B, 0xDB, 0xFA, 0xCE, 0x92, 0x85, 0x1D, 0xD5, 0x2D, 0xA4, 0xF0, 0xCB, 0x2A, 0x3D, 0x74, 0x80, 0x1B, 0x3B, 0xDB, 0xFA, 0xCD, 0xC5, 0x5C, 0x47, 0x77, 0xE7, 0x97, 0x87, 0xB6, 0x5A, 0xAD, 0x24, 0x6F, 0x7E, 0x82, 0xB6, 0x5A, 0xAD, 0x25, 0x3D, 0x75, 0x4C, 0x78, 0xB4, 0xC0, 0x5B, 0x7B, 0x1A, 0x6D, 0xE4, 0x2F, 0x3E, 0x42, 0x76, 0x1A, 0x6D, 0xE4, 0x30, 0x0C, 0x37, 0xA7, 0x57, 0x47, 0x76, 0x1A, 0x6E, 0xB1, 0x59, 0xE1, 0xC9, 0x91, 0xB9, 0xC1, 0x28, 0xA3, 0x22, 0xD5, 0x2C, 0xD7, 0xC7, 0xF6, 0x99, 0x21, 0x08, 0x03, 0x02, 0x35, 0x0C, 0x38, 0x73, 0xB3, 0xF2, 0x66, 0x49, 0x10, 0x6C, 0x17, 0x06, 0x6A, 0x7E, 0x82, 0xB5, 0x8C, 0xB8, 0xF4, 0x00, 0x9C, 0x87, 0xB6, 0x59, 0xE1, 0xC9, 0x90, 0xEC, 0x97, 0x87, 0xB7, 0x26, 0x0A, 0x9E, 0x21, 0x09, 0xD1, 0xF9, 0x01, 0x68, 0xE4, 0x2F, 0x3F, 0x0F, 0x9F, 0xEF, 0xFF, 0xCE, 0x92, 0x86, 0xE9, 0x31, 0xD8, 0x94, 0x20, 0x3B, 0xDB, 0xFA, 0xCE, 0x92, 0x85, 0x1C, 0x08, 0x03, 0x02, 0x36, 0xD9, 0x60, 0x7C, 0xE8, 0x63, 0xE3, 0x62, 0x15, 0x6D, 0xE5, 0xFD, 0x34, 0x3F, 0x0F, 0x9F, 0xEF, 0xFE, 0x02, 0x36, 0xDA, 0x2D, 0xA4, 0xEF, 0xFE, 0x01, 0x69, 0xB1, 0x59, 0xE0, 0xFB, 0x9B, 0xBA, 0x8D, 0x85, 0x1D, 0xD4, 0x60, 0x7B, 0x1B, 0x3B, 0xDB, 0xFB, 0x9A, 0xEE, 0x32, 0xA5, 0xBC, 0x28, 0xA3, 0x23, 0xA3, 0x23, 0xA3, 0x23, 0xA3, 0x22, 0xD6, 0xFA, 0xCE, 0x92, 0x86, 0xE9, 0x30, 0x0C, 0x38, 0x74, 0x7F, 0x4F, 0xDF, 0x2F, 0x3E, 0x41, 0xA8, 0x23, 0xA3, 0x23, 0xA3, 0x22, 0xD5, 0x2D, 0xA4, 0xF0, 0xCC, 0xF7, 0x67, 0x16, 0x39, 0x40, 0xDB, 0xFB, 0x9B, 0xBA, 0x8D, 0x84, 0x4F, 0xDE, 0x62, 0x16, 0x39, 0x40, 0xDC, 0xC7, 0xF6, 0x99, 0x21, 0x08, 0x04, 0xD0, 0x2C, 0xD8, 0x94, 0x1F, 0x6F, 0x7E, 0x82, 0xB5, 0x8D, 0x85, 0x1C, 0x08, 0x04, 0xD0, 0x2C, 0xD8, 0x93, 0x53, 0x12, 0x05, 0x9C, 0x88, 0x84, 0x4F, 0xDE, 0x61, 0x48, 0x44, 0x0F, 0x9E, 0x22, 0xD5, 0x2D, 0xA5, 0xBC, 0x28, 0xA4, 0xF0, 0xCB, 0x2B, 0x0A, 0x9D, 0x55, 0xAC, 0x58, 0x14, 0xA0, 0xBC, 0x28, 0xA3, 0x22, 0xD6, 0xF9, 0x00, 0x9B, 0xBA, 0x8E, 0x52, 0x45, 0xDC, 0xC7, 0xF7, 0x67, 0x17, 0x06, 0x69, 0xB1, 0x58, 0x13, 0xD2, 0xC6, 0x29, 0x71, 0x18, 0xD4, 0x5F, 0xAE, 0xF1, 0x98, 0x54, 0xE0, 0xFC, 0x68, 0xE4, 0x2F, 0x3F, 0x0E, 0xD1, 0xF9, 0x01, 0x69, 0xB1, 0x58, 0x14, 0x9F, 0xEE, 0x32, 0xA5, 0xBD, 0xF4, 0xFF, 0xCE, 0x91, 0xB9, 0xC0, 0x5B, 0x7B, 0x1B, 0x3A, 0x0D, 0x05, 0x9C, 0x87, 0xB6, 0x5A, 0xAE, 0xF2, 0x65, 0x7C, 0xE8, 0x63, 0xE3, 0x62, 0x15, 0x6C, 0x17, 0x07, 0x36, 0xD9, 0x61, 0x48, 0x43, 0x43, 0x42, 0x75, 0x4C, 0x78, 0xB3, 0xF3, 0x33, 0x72, 0xE6, 0xCA, 0x5E, 0xE1, 0xC8, 0xC3, 0xC3, 0xC3, 0xC2, 0xF6, 0x99, 0x21, 0x08, 0x04, 0xD0, 0x2C, 0xD8, 0x94, 0x1F, 0x6E, 0xB2, 0x26, 0x0A, 0x9E, 0x22, 0xD5, 0x2D, 0xA4, 0xEF, 0xFF, 0xCF, 0x5F, 0xAF, 0xBE, 0xC2, 0xF5, 0xCC, 0xF7, 0x66, 0x4A, 0xDE, 0x61, 0x49, 0x11, 0x39, 0x41, 0xA8, 0x24, 0x70, 0x4C, 0x77, 0xE7, 0x97, 0x86, 0xEA, 0xFD, 0x34, 0x40, 0xDB, 0xFA, 0xCE, 0x92, 0x86, 0xE9, 0x31, 0xD8, 0x93, 0x52, 0x46, 0xAA, 0xBD, 0xF5, 0xCD, 0xC5, 0x5D, 0x14, 0xA0, 0xBB, 0x5A, 0xAE, 0xF2, 0x65, 0x7C, 0xE7, 0x97, 0x86, 0xEA, 0xFD, 0x34, 0x3F, 0x0E, 0xD2, 0xC5, 0x5D, 0x15, 0x6D, 0xE5, 0xFD, 0x35, 0x0C, 0x37, 0xA7, 0x57, 0x47, 0x77, 0xE7, 0x97, 0x87, 0xB6, 0x59, 0xE1, 0xC8, 0xC4, 0x8F, 0x1E, 0xA2, 0x55, 0xAD, 0x24, 0x70, 0x4C, 0x77, 0xE7, 0x96, 0xB9, 0xC0, 0x5C, 0x47, 0x76, 0x1A, 0x6D, 0xE4, 0x2F, 0x3E, 0x41, 0xA9, 0xF1, 0x98, 0x53, 0x12, 0x06, 0x69, 0xB0, 0x8C, 0xB7, 0x26, 0x0A, 0x9D, 0x54, 0xDF, 0x2E, 0x72, 0xE5, 0xFD, 0x34, 0x3F, 0x0F, 0x9F, 0xEE, 0x32, 0xA5, 0xBD, 0xF4, 0xFF, 0xCF, 0x5E, 0xE1, 0xC9, 0x91, 0xB9, 0xC0, 0x5C, 0x48, 0x43, 0x42, 0x75, 0x4C, 0x78, 0xB3, 0xF2, 0x65, 0x7C, 0xE7, 0x96, 0xB9, 0xC1, 0x28, 0xA3, 0x22, 0xD5, 0x2D, 0xA5, 0xBC, 0x27, 0xD6, 0xF9, 0x01, 0x69, 0xB1, 0x58, 0x13, 0xD2, 0xC6, 0x2A, 0x3D, 0x75, 0x4D, 0x45, 0xDC, 0xC7, 0xF6, 0x99, 0x21, 0x09, 0xD0, 0x2C, 0xD7, 0xC7, 0xF7, 0x67, 0x16, 0x39, 0x41, 0xA8, 0x24, 0x6F, 0x7E, 0x82, 0xB6, 0x59, 0xE1, 0xC9, 0x90, 0xEC, 0x98, 0x53, 0x12, 0x05, 0x9C, 0x87, 0xB6, 0x5A, 0xAD, 0x25, 0x3C, 0xA8, 0x24, 0x70, 0x4C, 0x77, 0xE6, 0xCA, 0x5E, 0xE2, 0x95, 0xED, 0x64, 0xB0, 0x8B, 0xEB, 0xCB, 0x2B, 0x0A, 0x9D, 0x55, 0xAC, 0x58, 0x13, 0xD3, 0x92, 0x86, 0xEA, 0xFD, 0x34, 0x3F, 0x0E, 0xD1, 0xF8, 0x34, 0x40, 0xDC, 0xC8, 0xC4, 0x8F, 0x1E, 0xA1, 0x89, 0x50, 0xAB, 0x8A, 0x1D, 0xD5, 0x2D, 0xA4, 0xF0, 0xCB, 0x2B, 0x0A, 0x9D, 0x55, 0xAC, 0x57, 0x46, 0xA9, 0xF0, 0xCC, 0xF7, 0x67, 0x17, 0x07, 0x36, 0xDA, 0x2E, 0x71, 0x19, 0xA1, 0x88, 0x83, 0x83, 0x83, 0x82, 0xB6, 0x5A, 0xAD, 0x25, 0x3D, 0x74, 0x80, 0x1C, 0x08, 0x04, 0xCF, 0x5F, 0xAF, 0xBF, 0x8E, 0x51, 0x78, 0xB3, 0xF3, 0x32, 0xA5, 0xBD, 0xF5, 0xCD, 0xC4, 0x90, 0xEC, 0x97, 0x87, 0xB7, 0x27, 0xD7, 0xC6, 0x29, 0x70, 0x4B, 0xAB, 0x8B, 0xEB, 0xCB, 0x2A, 0x3D, 0x74, 0x7F, 0x4F, 0xDE, 0x62, 0x15, 0x6D, 0xE5, 0xFD, 0x34, 0x40, 0xDB, 0xFA, 0xCD, 0xC4, 0x90, 0xEB, 0xCA, 0x5E, 0xE1, 0xC9, 0x91, 0xB9, 0xC1, 0x28, 0xA4, 0xEF, 0xFF, 0xCE, 0x92, 0x85, 0x1D, 0xD4, 0x5F, 0xAE, 0xF2, 0x65, 0x7D, 0xB5, 0x8D, 0x84, 0x50, 0xAC, 0x57, 0x47, 0x76, 0x1A, 0x6E, 0xB1, 0x59, 0xE0, 0xFB, 0x9B, 0xBB, 0x5B, 0x7A, 0x4D, 0x45, 0xDD, 0x95, 0xED, 0x65, 0x7D, 0xB4, 0xBF, 0x8F, 0x1F, 0x6F, 0x7E, 0x81, 0xE9, 0x30, 0x0C, 0x37, 0xA6, 0x89, 0x50, 0xAC, 0x57, 0x46, 0xAA, 0xBD, 0xF5, 0xCC, 0xF7, 0x66, 0x4A, 0xDE, 0x61, 0x48, 0x44, 0x10, 0x6C, 0x18, 0xD4, 0x5F, 0xAF, 0xBE, 0xC1, 0x28, 0xA3, 0x23, 0xA2, 0x55, 0xAC, 0x58, 0x14, 0xA0, 0xBC, 0x28, 0xA4, 0xEF, 0xFF, 0xCF, 0x5E, 0xE1, 0xC8, 0xC4, 0x8F, 0x1E, 0xA1, 0x88, 0x83, 0x82, 0xB5, 0x8C, 0xB7, 0x27, 0xD6, 0xF9, 0x00, 0x9C, 0x87, 0xB6, 0x59, 0xE1, 0xC9, 0x90, 0xEC, 0x98, 0x53, 0x13, 0xD3, 0x93, 0x53, 0x12, 0x06, 0x6A, 0x7D, 0xB5, 0x8C, 0xB8, 0xF4, 0xFF, 0xCF, 0x5F, 0xAF, 0xBE, 0xC2, 0xF5, 0xCD, 0xC4, 0x8F, 0x1F, 0x6E, 0xB1, 0x59, 0xE1, 0xC8, 0xC4, 0x90, 0xEB, 0xCA, 0x5E, 0xE2, 0x95, 0xED, 0x64, 0xAF, 0xBE, 0xC1, 0x28, 0xA3, 0x23, 0xA3, 0x23, 0xA3, 0x23, 0xA2, 0x55, 0xAD, 0x25, 0x3D, 0x74, 0x7F, 0x4F, 0xDE, 0x62, 0x16, 0x39, 0x40, 0xDC, 0xC7, 0xF7, 0x67, 0x17, 0x06, 0x69, 0xB1, 0x58, 0x13, 0xD3, 0x93, 0x53, 0x13, 0xD2, 0xC5, 0x5C, 0x47, 0x77];
}
