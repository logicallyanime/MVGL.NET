namespace MVGLTools;


public static class Mdb1ArchivePlanner
{
    
    private static string GetMutationKey(string path) => $"mutation:{NormalizeArchivePath(path)}";

    public readonly record struct ArchiveMutation(string SourcePath, string EntryPath);

    public readonly record struct FileReference(string ArchivePath, string SourceKey, bool IsExisting, int ExistingDataIndex);

    public enum PayloadKind
    {
        ExistingArchive,
        SourceFile,
        TempStoredFile,
    }

    public readonly record struct PreparedDataSource(string Key, string ArchivePath, PayloadKind Kind, string? SourcePath, long SourceOffset, ulong FullSize, ulong StoredSize, ulong Offset)
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

    public static Mdb1ArchivePlan CreatePlanFromFolder<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, CompressMode compress)
    {
        throw new NotImplementedException();
    }
    public static Mdb1ArchivePlan CreatePlanFromFile<TProfile>(string archivePath, string sourcePath, string? entryPath, CompressMode compress)
    {
        throw new NotImplementedException();
    }
    
    public static Mdb1ArchivePlan AddFile<TProfile>(string archivePath, string sourcePath, string? entryPath, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        return ApplyArchiveMutations<TProfile>(archivePath, [new ArchiveMutation(sourcePath, entryPath ?? Path.GetFileName(sourcePath))], compress, requireExisting: false);
    }

    
    private static Mdb1ArchivePlan AddFolder<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, CompressMode compress)
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

        var mutations = GetArchiveMutations(sourceFolder, normalizedArchiveRoot);

        return ApplyArchiveMutations<TProfile>(archivePath, mutations, compress, false);
    }

    public static ArchiveMutation[] GetArchiveMutations(string sourceFolder, string? normalizedArchiveRoot)
    {
        var mutations = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(sourceFolder, path).Replace(Path.DirectorySeparatorChar, '/');
                var archiveRootPath = string.IsNullOrWhiteSpace(normalizedArchiveRoot)
                    ? relativePath
                    : $"{normalizedArchiveRoot}/{relativePath}";
                return new ArchiveMutation(path, archiveRootPath);
            })
            .ToArray();
        return mutations;
    }

    internal static Mdb1ArchivePlan ApplyArchiveMutations<TProfile>(
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
                return new Mdb1ArchivePlan(archivePath, finalFiles, preparedSources, dataEntries, fileDataIds, mutations);
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

    internal static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

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

public class Mdb1ArchivePlan
{
    private Mdb1ArchivePlanner.ArchiveMutation[] _archiveMutations { get; set;}
    private string _archivePath { get; set;}
    public List<Mdb1ArchivePlanner.FileReference> FinalFiles { get; private set;}
    public List<Mdb1ArchivePlanner.PreparedDataSource> PreparedSources { get; private set;}
    public Mdb1Format.DataEntry[] DataEntries { get; private set;}
    public Dictionary<string, int> FileDataIds { get; private set;}
    
    
    public Mdb1ArchivePlan(string archivePath, List<Mdb1ArchivePlanner.FileReference> finalFiles,
        List<Mdb1ArchivePlanner.PreparedDataSource> preparedSources, Mdb1Format.DataEntry[] dataEntries,
        Dictionary<string, int> fileDataIds, IReadOnlyList<Mdb1ArchivePlanner.ArchiveMutation> archiveMutations)
    {
        _archivePath = archivePath;
        FinalFiles = finalFiles;
        PreparedSources = preparedSources;
        DataEntries = dataEntries;
        FileDataIds = fileDataIds;
        _archiveMutations = archiveMutations.ToArray();
        
    }

    public void AddFolder<TProfile>(string sourceFolder, string? archiveRoot)
        where TProfile : IMdbProfile, new()
    {
        var normalizedArchiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
            ? null
            : Mdb1ArchivePlanner.NormalizeArchivePath(archiveRoot);

        var mutations = Mdb1ArchivePlanner.GetArchiveMutations(sourceFolder, normalizedArchiveRoot);

        var newMutations = _archiveMutations
            .UnionBy(mutations, static mutation => mutation.EntryPath, StringComparer.OrdinalIgnoreCase).ToArray();

        var _plan = Mdb1ArchivePlanner.ApplyArchiveMutations<TProfile>(_archivePath, newMutations, CompressMode.None, false);
        
        FinalFiles = _plan.FinalFiles;
        PreparedSources = _plan.PreparedSources;
        DataEntries = _plan.DataEntries;
        FileDataIds = _plan.FileDataIds;
        _archiveMutations = newMutations;
    }

}



static class Mdb1StreamingWriter
{
    
    public static void WriteArchiveToDisk<TProfile>(string archivePath, Mdb1ArchivePlan plan)
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
                    Mdb1Format.WriteArchiveMetadata(output, profile, plan.FinalFiles.Select(static file => file.ArchivePath).ToArray(), plan.FileDataIds, plan.DataEntries);

                    Helpers.Log($"[MDB1] Streaming rewrite of {Path.GetFileName(archivePath)} with {plan.PreparedSources.Count} data blobs...");
                    for (var i = 0; i < plan.PreparedSources.Count; i++)
                    {
                        var source = plan.PreparedSources[i];
                        if ((i + 1) % 200 == 0 || i + 1 == plan.PreparedSources.Count)
                        {
                            Helpers.Log($"[MDB1] Writing payload {i + 1}/{plan.PreparedSources.Count}");
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
            foreach (var source in plan.PreparedSources)
            {
                source.Dispose();
            }
        }
    }

    private static void WritePreparedSource(FileStream input, Stream output, IMdbProfile profile, Mdb1ArchivePlanner.PreparedDataSource source)
    {
        switch (source.Kind)
        {
            case Mdb1ArchivePlanner.PayloadKind.ExistingArchive:
                Mdb1Format.CopyStoredPayload(input, output, profile, source.SourceOffset, source.StoredSize);
                break;
            case Mdb1ArchivePlanner.PayloadKind.SourceFile:
                using (var fileInput = new FileStream(source.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.SequentialScan))
                {
                    CopyPlainPayload(fileInput, output, profile.Crypted);
                }

                break;
            case Mdb1ArchivePlanner.PayloadKind.TempStoredFile:
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

    
}
