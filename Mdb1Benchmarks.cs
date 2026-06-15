using System.Diagnostics;

namespace MVGLTools;

internal static class Mdb1Benchmarks
{
    public static void BenchmarkAdd<TProfile>(string archivePath, string sourcePath, string? entryPath, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive path does not exist.", archivePath);
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        var normalizedEntryPath = NormalizeArchivePath(entryPath ?? Path.GetFileName(sourcePath));
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"mvgltools-mdb1-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var legacyArchive = Path.Combine(tempDirectory, "legacy.mvgl");
        var streamingArchive = Path.Combine(tempDirectory, "streaming.mvgl");
        File.Copy(archivePath, legacyArchive, true);
        File.Copy(archivePath, streamingArchive, true);

        try
        {
            Helpers.Log($"[Benchmark] Testing add/update for '{normalizedEntryPath}'...");

            var legacyWatch = Stopwatch.StartNew();
            var legacy = Mdb1<TProfile>.Open(legacyArchive);
            legacy.AddFile(sourcePath, normalizedEntryPath);
            legacy.Write(legacyArchive, compress);
            legacyWatch.Stop();

            var streamingWatch = Stopwatch.StartNew();
            Mdb1<TProfile>.AddFileStreaming(streamingArchive, sourcePath, normalizedEntryPath, compress);
            streamingWatch.Stop();

            ValidateResult<TProfile>(legacyArchive, normalizedEntryPath, sourcePath, "legacy");
            ValidateResult<TProfile>(streamingArchive, normalizedEntryPath, sourcePath, "streaming");

            var legacySize = new FileInfo(legacyArchive).Length;
            var streamingSize = new FileInfo(streamingArchive).Length;
            var improvement = legacyWatch.Elapsed.TotalMilliseconds <= 0
                ? 0
                : ((legacyWatch.Elapsed.TotalMilliseconds - streamingWatch.Elapsed.TotalMilliseconds) / legacyWatch.Elapsed.TotalMilliseconds) * 100.0;

            Helpers.Log($"[Benchmark] Legacy add/write:    {legacyWatch.Elapsed.TotalMilliseconds:N0} ms");
            Helpers.Log($"[Benchmark] Streaming rewrite:  {streamingWatch.Elapsed.TotalMilliseconds:N0} ms");
            Helpers.Log($"[Benchmark] Improvement:       {improvement:N1}%");
            Helpers.Log($"[Benchmark] Legacy size:       {legacySize:N0} bytes");
            Helpers.Log($"[Benchmark] Streaming size:    {streamingSize:N0} bytes");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, true);
            }
            catch
            {
            }
        }
    }

    public static void BenchmarkAddFolder<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, CompressMode compress)
        where TProfile : IMdbProfile, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive path does not exist.", archivePath);
        }

        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("Source folder does not exist.");
        }

        var normalizedArchiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
            ? null
            : NormalizeArchivePath(archiveRoot);
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"mvgltools-mdb1-bench-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var legacyArchive = Path.Combine(tempDirectory, "legacy.mvgl");
        var streamingArchive = Path.Combine(tempDirectory, "streaming.mvgl");
        File.Copy(archivePath, legacyArchive, true);
        File.Copy(archivePath, streamingArchive, true);

        try
        {
            Helpers.Log($"[Benchmark] Testing folder add/update for '{sourceFolder}'...");

            var legacyWatch = Stopwatch.StartNew();
            var legacy = Mdb1<TProfile>.Open(legacyArchive);
            legacy.AddFolder(sourceFolder, normalizedArchiveRoot);
            legacy.Write(legacyArchive, compress);
            legacyWatch.Stop();

            var streamingWatch = Stopwatch.StartNew();
            Mdb1<TProfile>.AddFolderStreaming(streamingArchive, sourceFolder, normalizedArchiveRoot, compress);
            streamingWatch.Stop();

            ValidateFolderResult<TProfile>(legacyArchive, sourceFolder, normalizedArchiveRoot, "legacy");
            ValidateFolderResult<TProfile>(streamingArchive, sourceFolder, normalizedArchiveRoot, "streaming");

            var legacySize = new FileInfo(legacyArchive).Length;
            var streamingSize = new FileInfo(streamingArchive).Length;
            var improvement = legacyWatch.Elapsed.TotalMilliseconds <= 0
                ? 0
                : ((legacyWatch.Elapsed.TotalMilliseconds - streamingWatch.Elapsed.TotalMilliseconds) / legacyWatch.Elapsed.TotalMilliseconds) * 100.0;

            Helpers.Log($"[Benchmark] Legacy folder add:   {legacyWatch.Elapsed.TotalMilliseconds:N0} ms");
            Helpers.Log($"[Benchmark] Streaming rewrite:  {streamingWatch.Elapsed.TotalMilliseconds:N0} ms");
            Helpers.Log($"[Benchmark] Improvement:       {improvement:N1}%");
            Helpers.Log($"[Benchmark] Legacy size:       {legacySize:N0} bytes");
            Helpers.Log($"[Benchmark] Streaming size:    {streamingSize:N0} bytes");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, true);
            }
            catch
            {
            }
        }
    }

    private static void ValidateResult<TProfile>(string archivePath, string entryPath, string sourcePath, string label)
        where TProfile : IMdbProfile, new()
    {
        var expected = File.ReadAllBytes(sourcePath);
        var actual = Mdb1<TProfile>.Open(archivePath).ReadFileData(entryPath);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"Benchmark validation failed for the {label} archive.");
        }
    }

    private static void ValidateFolderResult<TProfile>(string archivePath, string sourceFolder, string? archiveRoot, string label)
        where TProfile : IMdbProfile, new()
    {
        var normalizedArchiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
            ? null
            : NormalizeArchivePath(archiveRoot);
        var archive = Mdb1<TProfile>.Open(archivePath);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            var entryPath = string.IsNullOrWhiteSpace(normalizedArchiveRoot)
                ? relativePath
                : $"{normalizedArchiveRoot}/{relativePath}";
            var expected = File.ReadAllBytes(sourcePath);
            var actual = archive.ReadFileData(entryPath);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Folder benchmark validation failed for the {label} archive at '{entryPath}'.");
            }
        }
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
