namespace MVGLTools;


static class Mdb1ArchivePlanner
{
    private static Mdb1ArchivePlan _plan;

    public static Mdb1ArchivePlan CreatePlan()
    {
        throw new NotImplementedException();
    }

    
    public static void AddFolder<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);

        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("Source folder does not exist.");
        }

        var normalizedArchiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
            ? null
            : NormalizeArchivePath(archiveRoot);

        var mutations = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(sourceFolder, path).Replace(Path.DirectorySeparatorChar, '/');
                var archivePath = string.IsNullOrWhiteSpace(normalizedArchiveRoot)
                    ? relativePath
                    : $"{normalizedArchiveRoot}/{relativePath}";
                return new ArchiveMutation(path, archivePath);
            })
            .ToArray();

        RewriteArchive<TProfile>(archivePath, mutations, compress, requireExisting: false);
    }
    
    public static (List<FileReference> finalFiles, 
        List<PreparedDataSource> preparedSources, 
        Mdb1Format.DataEntry[] dataEntries, 
        Dictionary<string, int> fileDataIds) 
        
        ApplyArchiveMutations<TProfile>(
            string archivePath, 
            IReadOnlyList<ArchiveMutation> mutations,
            CompressMode compress, 
            bool requireExisting)
        where TProfile : IMdbProfile, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(mutations);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive path does not exist.", archivePath);
        }

        if (mutations.Count == 0)
        {
            throw new ArgumentException("At least one source file is required.", nameof(mutations));
        }

        foreach (var mutation in mutations)
        {
            if (!File.Exists(mutation.SourcePath))
            {
                throw new FileNotFoundException("Source path does not exist.", mutation.SourcePath);
            }

            if (string.IsNullOrWhiteSpace(mutation.EntryPath))
            {
                throw new ArgumentException("Archive entry paths must not be empty.", nameof(mutations));
            }
        }

        var profile = new TProfile();
        var index = Mdb1Format.ReadArchiveIndex(archivePath, profile);
        var normalizedMutations = mutations
            .Select(static mutation =>
                new ArchiveMutation(mutation.SourcePath, NormalizeArchivePath(mutation.EntryPath)))
            .ToArray();
        var mutationMap = normalizedMutations
            .GroupBy(static mutation => mutation.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
        var preparedMutationMap = new Dictionary<string, PreparedDataSource>(StringComparer.OrdinalIgnoreCase);
        var finalFiles = new List<FileReference>(index.Files.Count + mutationMap.Count);
        var unmatchedMutations = new HashSet<string>(mutationMap.Keys, StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in index.Files)
            {
                if (mutationMap.TryGetValue(file.ArchivePath, out var mutation))
                {
                    var mutationKey = GetMutationKey(mutation.EntryPath);
                    finalFiles.Add(new FileReference(mutation.EntryPath, mutationKey, false, -1));
                    unmatchedMutations.Remove(mutation.EntryPath);

                    if (!preparedMutationMap.ContainsKey(mutation.EntryPath))
                    {
                        preparedMutationMap[mutation.EntryPath] =
                            PrepareMutation(mutation.SourcePath, mutation.EntryPath, profile.Compressor, compress) with
                            {
                                Key = mutationKey
                            };
                    }

                    continue;
                }

                finalFiles.Add(new FileReference(file.ArchivePath, $"existing:{file.DataIndex}", true, file.DataIndex));
            }

            if (requireExisting && unmatchedMutations.Count > 0)
            {
                throw new FileNotFoundException(
                    $"File '{unmatchedMutations.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).First()}' does not exist in the archive.");
            }

            foreach (var entryPath in unmatchedMutations.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                var mutation = mutationMap[entryPath];
                var mutationKey = GetMutationKey(mutation.EntryPath);
                finalFiles.Add(new FileReference(mutation.EntryPath, mutationKey, false, -1));
                preparedMutationMap[mutation.EntryPath] =
                    PrepareMutation(mutation.SourcePath, mutation.EntryPath, profile.Compressor, compress) with
                    {
                        Key = mutationKey
                    };
            }

            var preparedSources = new List<PreparedDataSource>();
            var sourceIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileDataIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ulong currentOffset = 0;

            try
            {
                foreach (var file in finalFiles)
                {
                    if (!sourceIds.TryGetValue(file.SourceKey, out var dataId))
                    {
                        var source = file.IsExisting
                            ? PreparedDataSource.FromExisting(file.SourceKey, archivePath, index.Header.DataStart,
                                index.DataEntries[file.ExistingDataIndex], file.ExistingDataIndex)
                            : preparedMutationMap[file.ArchivePath];

                        source = source with { Offset = currentOffset };
                        currentOffset += source.StoredSize;
                        dataId = preparedSources.Count;
                        preparedSources.Add(source);
                        sourceIds[file.SourceKey] = dataId;
                    }

                    fileDataIds[file.ArchivePath] = dataId;
                }

                var dataEntries = preparedSources
                    .Select(static source =>
                        new Mdb1Format.DataEntry(source.Offset, source.FullSize, source.StoredSize))
                    .ToArray();
                return (finalFiles, preparedSources, dataEntries, fileDataIds);
            }
            catch
            {
                foreach (var preparedMutation in preparedMutationMap.Values)
                {
                    preparedMutation.Dispose();
                }

                throw;
            }
        }catch
        {
            foreach (var preparedMutation in preparedMutationMap.Values)
            {
                preparedMutation.Dispose();
            }

            throw;
        }
        
    }
    
    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static PreparedDataSource PrepareMutation(string sourcePath, string archivePath, ICompressor compressor, CompressMode compress)
    {
        var fileInfo = new FileInfo(sourcePath);
        if (compress == CompressMode.None || fileInfo.Length == 0)
        {
            return PreparedDataSource.FromSourceFile(string.Empty, archivePath, sourcePath, (ulong)fileInfo.Length);
        }

        var rawData = File.ReadAllBytes(sourcePath);
        var storedData = compressor.Compress(rawData);
        if (storedData.Length + 4 >= rawData.Length)
        {
            return PreparedDataSource.FromSourceFile(string.Empty, archivePath, sourcePath, (ulong)rawData.Length);
        }

        var tempPayloadPath = Path.GetTempFileName();
        File.WriteAllBytes(tempPayloadPath, storedData);
        return PreparedDataSource.FromTempFile(string.Empty, archivePath, tempPayloadPath, (ulong)rawData.Length, (ulong)storedData.Length);
    }
}

class Mdb1ArchivePlan
{
    public List<Mdb1Streaming.FileReference> finalFiles { get; private set; }
    public List<Mdb1Streaming.PreparedDataSource> preparedSources { get; private set; }
    public Mdb1Format.DataEntry[] dataEntries { get; private set; }
    public Dictionary<string, int> fileDataIds { get; private set; }
    
    
    public Mdb1ArchivePlan(List<Mdb1Streaming.FileReference> finalFiles,
        List<Mdb1Streaming.PreparedDataSource> preparedSources, Mdb1Format.DataEntry[] dataEntries,
        Dictionary<string, int> fileDataIds)
    {
        this.finalFiles = finalFiles;
        this.preparedSources = preparedSources;
        this.dataEntries = dataEntries;
        this.fileDataIds = fileDataIds;
    }

}



internal static class Mdb1Streaming
{
    public static void AddFile<TProfile>(string archivePath, string sourcePath, string? entryPath, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        RewriteArchive<TProfile>(archivePath, [new ArchiveMutation(sourcePath, entryPath ?? Path.GetFileName(sourcePath))], compress, requireExisting: false);
    }

    public static void UpdateFile<TProfile>(string archivePath, string sourcePath, string entryPath, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        RewriteArchive<TProfile>(archivePath, [new ArchiveMutation(sourcePath, entryPath)], compress, requireExisting: true);
    }

    public static void AddFolder<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);

        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("Source folder does not exist.");
        }

        var normalizedArchiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
            ? null
            : NormalizeArchivePath(archiveRoot);

        var mutations = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(sourceFolder, path).Replace(Path.DirectorySeparatorChar, '/');
                var archivePath = string.IsNullOrWhiteSpace(normalizedArchiveRoot)
                    ? relativePath
                    : $"{normalizedArchiveRoot}/{relativePath}";
                return new ArchiveMutation(path, archivePath);
            })
            .ToArray();

        RewriteArchive<TProfile>(archivePath, mutations, compress, requireExisting: false);
    }

    

    private static void RewriteArchive<TProfile>(string archivePath, Mdb1ArchivePlan plan)
        where TProfile : IMdbProfile, new()
    {
        
        try
        {
            var profile = new TProfile();
            var tempPath = CreateTempPath(archivePath);

            try
            {
                using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.SequentialScan))
                using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 0x10000))
                {
                    Mdb1Format.WriteArchiveMetadata(output, profile, plan.finalFiles.Select(static file => file.ArchivePath).ToArray(), plan.fileDataIds, plan.dataEntries);

                    Helpers.Log($"[MDB1] Streaming rewrite of {Path.GetFileName(archivePath)} with {plan.preparedSources.Count} data blobs...");
                    for (var i = 0; i < plan.preparedSources.Count; i++)
                    {
                        var source = plan.preparedSources[i];
                        if ((i + 1) % 200 == 0 || i + 1 == plan.preparedSources.Count)
                        {
                            Helpers.Log($"[MDB1] Writing payload {i + 1}/{plan.preparedSources.Count}");
                        }

                        WritePreparedSource(input, output, profile, source);
                    }

                    output.Flush();
                }

                ReplaceFile(tempPath, archivePath);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            foreach (var source in plan.preparedSources)
            {
                source.Dispose();
            }
        }
    }

    private static PreparedDataSource PrepareMutation(string sourcePath, string archivePath, ICompressor compressor, CompressMode compress)
    {
        var fileInfo = new FileInfo(sourcePath);
        if (compress == CompressMode.None || fileInfo.Length == 0)
        {
            return PreparedDataSource.FromSourceFile(string.Empty, archivePath, sourcePath, (ulong)fileInfo.Length);
        }

        var rawData = File.ReadAllBytes(sourcePath);
        var storedData = compressor.Compress(rawData);
        if (storedData.Length + 4 >= rawData.Length)
        {
            return PreparedDataSource.FromSourceFile(string.Empty, archivePath, sourcePath, (ulong)rawData.Length);
        }

        var tempPayloadPath = Path.GetTempFileName();
        File.WriteAllBytes(tempPayloadPath, storedData);
        return PreparedDataSource.FromTempFile(string.Empty, archivePath, tempPayloadPath, (ulong)rawData.Length, (ulong)storedData.Length);
    }

    private static void WritePreparedSource(FileStream input, Stream output, IMdbProfile profile, PreparedDataSource source)
    {
        switch (source.Kind)
        {
            case PayloadKind.ExistingArchive:
                Mdb1Format.CopyStoredPayload(input, output, profile, source.SourceOffset, source.StoredSize);
                break;
            case PayloadKind.SourceFile:
                using (var fileInput = new FileStream(source.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.SequentialScan))
                {
                    CopyPlainPayload(fileInput, output, profile.Crypted);
                }

                break;
            case PayloadKind.TempStoredFile:
                using (var fileInput = new FileStream(source.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.SequentialScan))
                {
                    CopyPlainPayload(fileInput, output, profile.Crypted);
                }

                break;
            default:
                throw new InvalidOperationException("Unknown payload source kind.");
        }
    }

    private static void CopyPlainPayload(Stream input, Stream output, bool crypt)
    {
        var buffer = new byte[0x10000];
        while (true)
        {
            var bytesRead = input.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            Helpers.WriteBytes(output, buffer.AsSpan(0, bytesRead), crypt);
        }
    }

    private static void ReplaceFile(string tempPath, string archivePath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Replace(tempPath, archivePath, null, true);
            return;
        }

        File.Move(tempPath, archivePath, true);
    }

    private static string CreateTempPath(string archivePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? Environment.CurrentDirectory;
        return Path.Combine(directory, $"{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string GetMutationKey(string path) => $"mutation:{NormalizeArchivePath(path)}";

    internal readonly record struct ArchiveMutation(string SourcePath, string EntryPath);

    internal readonly record struct FileReference(string ArchivePath, string SourceKey, bool IsExisting, int ExistingDataIndex);

    internal enum PayloadKind
    {
        ExistingArchive,
        SourceFile,
        TempStoredFile,
    }

    internal readonly record struct PreparedDataSource(string Key, string ArchivePath, PayloadKind Kind, string? SourcePath, long SourceOffset, ulong FullSize, ulong StoredSize, ulong Offset)
    {
        public static PreparedDataSource FromExisting(string key, string sourceArchivePath, ulong dataStart, Mdb1Format.DataEntry dataEntry, int dataIndex)
            => new(key, $"existing:{dataIndex}", PayloadKind.ExistingArchive, sourceArchivePath, checked((long)(dataStart + dataEntry.Offset)), dataEntry.FullSize, dataEntry.CompressedSize, 0);

        public static PreparedDataSource FromSourceFile(string key, string archivePath, string sourcePath, ulong size)
            => new(key, archivePath, PayloadKind.SourceFile, sourcePath, 0, size, size, 0);

        public static PreparedDataSource FromTempFile(string key, string archivePath, string tempPath, ulong fullSize, ulong storedSize)
            => new(key, archivePath, PayloadKind.TempStoredFile, tempPath, 0, fullSize, storedSize, 0);

        public void Dispose()
        {
            if (Kind != PayloadKind.TempStoredFile || string.IsNullOrEmpty(SourcePath))
            {
                return;
            }

            try
            {
                if (File.Exists(SourcePath))
                {
                    File.Delete(SourcePath);
                }
            }
            catch
            {
            }
        }
    }
}
