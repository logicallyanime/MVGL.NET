using System.Globalization;

namespace MVGLTools;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "afs2" => RunAfs2(args[1..]),
                "save" => RunSave(args[1..]),
                "expa" => RunExpa(args[1..]),
                "mdb1" => RunMdb1(args[1..]),
                "help" or "-h" or "--help" => PrintHelpAndReturn(),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunAfs2(string[] args)
    {
        RequireLength(args, 3, "afs2 <extract|pack> <source> <target>");
        switch (args[0].ToLowerInvariant())
        {
            case "extract":
                Afs2.Extract(args[1], args[2]);
                return 0;
            case "pack":
                Afs2.Pack(args[1], args[2]);
                return 0;
            default:
                return Fail($"Unknown AFS2 action '{args[0]}'.");
        }
    }

    private static int RunSave(string[] args)
    {
        RequireLength(args, 3, "save <decrypt|encrypt> <source> <target>");
        switch (args[0].ToLowerInvariant())
        {
            case "decrypt":
                SaveFile.Decrypt(args[1], args[2]);
                return 0;
            case "encrypt":
                SaveFile.Encrypt(args[1], args[2]);
                return 0;
            default:
                return Fail($"Unknown save action '{args[0]}'.");
        }
    }

    private static int RunExpa(string[] args)
    {
        RequireLength(args, 4, "expa <export-csv|import-csv> <profile> <source> <target>");
        var profile = ParseExpaProfile(args[1]);

        switch (args[0].ToLowerInvariant())
        {
            case "export-csv":
                profile.Read(args[2], args[3]);
                return 0;
            case "import-csv":
                profile.Write(args[2], args[3]);
                return 0;
            default:
                return Fail($"Unknown EXPA action '{args[0]}'.");
        }
    }

    private static int RunMdb1(string[] args)
    {
        RequireLength(args, 1, "mdb1 <extract|extract-file|pack|add-file-stream|add-folder-stream|benchmark-add|benchmark-folder-add> ...");
        var action = args[0].ToLowerInvariant();
        return action switch
        {
            "extract" => RunMdb1Extract(args[1..]),
            "extract-file" => RunMdb1ExtractFile(args[1..]),
            "pack" => RunMdb1Pack(args[1..]),
            "add-file-stream" => RunMdb1AddFileStream(args[1..]),
            "add-folder-stream" => RunMdb1AddFolderStream(args[1..]),
            "benchmark-add" => RunMdb1BenchmarkAdd(args[1..]),
            "benchmark-folder-add" => RunMdb1BenchmarkAddFolder(args[1..]),
            _ => Fail($"Unknown MDB1 action '{args[0]}'.")
        };
    }

    private static int RunMdb1Extract(string[] args)
    {
        RequireLength(args, 3, "mdb1 extract <profile> <source> <target>");
        var profile = ParseMdbProfile(args[0]);
        profile.Extract(args[1], args[2]);
        return 0;
    }

    private static int RunMdb1ExtractFile(string[] args)
    {
        RequireLength(args, 4, "mdb1 extract-file <profile> <source> <file-in-archive> <target>");
        var profile = ParseMdbProfile(args[0]);
        profile.ExtractSingle(args[1], args[2], args[3]);
        return 0;
    }

    private static int RunMdb1Pack(string[] args)
    {
        if (args.Length is not 3 and not 4)
        {
            throw new ArgumentException("Usage: mdb1 pack <profile> [compress-mode] <source> <target>");
        }

        var profile = ParseMdbProfile(args[0]);
        var mode = args.Length == 4 ? ParseCompressMode(args[1]) : CompressMode.Normal;
        var sourceIndex = args.Length == 4 ? 2 : 1;
        profile.Pack(args[sourceIndex], args[sourceIndex + 1], mode);
        return 0;
    }

    private static int RunMdb1AddFileStream(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            throw new ArgumentException("Usage: mdb1 add-file-stream <profile> [compress-mode] <archive> <source-file> [file-in-archive]");
        }

        var profile = ParseMdbProfile(args[0]);
        var (mode, valueIndex) = ParseOptionalCompressMode(args, 1);
        var archiveEntryPath = args.Length - valueIndex >= 3 ? args[valueIndex + 2] : null;
        profile.AddFileStreaming(args[valueIndex], args[valueIndex + 1], archiveEntryPath, mode);
        return 0;
    }

    private static int RunMdb1BenchmarkAdd(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            throw new ArgumentException("Usage: mdb1 benchmark-add <profile> [compress-mode] <archive> <source-file> [file-in-archive]");
        }

        var profile = ParseMdbProfile(args[0]);
        var (mode, valueIndex) = ParseOptionalCompressMode(args, 1);
        var archiveEntryPath = args.Length - valueIndex >= 3 ? args[valueIndex + 2] : null;
        profile.BenchmarkAdd(args[valueIndex], args[valueIndex + 1], archiveEntryPath, mode);
        return 0;
    }

    private static int RunMdb1AddFolderStream(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            throw new ArgumentException("Usage: mdb1 add-folder-stream <profile> [compress-mode] <archive> <source-folder> [archive-root]");
        }

        var profile = ParseMdbProfile(args[0]);
        var (mode, valueIndex) = ParseOptionalCompressMode(args, 1);
        var archiveRoot = args.Length - valueIndex >= 3 ? args[valueIndex + 2] : null;
        profile.AddFolderStreaming(args[valueIndex], args[valueIndex + 1], archiveRoot, mode);
        return 0;
    }

    private static int RunMdb1BenchmarkAddFolder(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            throw new ArgumentException("Usage: mdb1 benchmark-folder-add <profile> [compress-mode] <archive> <source-folder> [archive-root]");
        }

        var profile = ParseMdbProfile(args[0]);
        var (mode, valueIndex) = ParseOptionalCompressMode(args, 1);
        var archiveRoot = args.Length - valueIndex >= 3 ? args[valueIndex + 2] : null;
        profile.BenchmarkAddFolder(args[valueIndex], args[valueIndex + 1], archiveRoot, mode);
        return 0;
    }

    private static ExpaProfileRunner ParseExpaProfile(string value) => value.ToLowerInvariant() switch
    {
        "dscs" => new ExpaProfileRunner(
            static (source, target) => Expa.ExportCsv(Expa.Read<DscsExpaProfile>(source), target),
            static (source, target) => Expa.Write<DscsExpaProfile>(Expa.ImportCsv<DscsExpaProfile>(source), target)),
        "dsts" => new ExpaProfileRunner(
            static (source, target) => Expa.ExportCsv(Expa.Read<DstsExpaProfile>(source), target),
            static (source, target) => Expa.Write<DstsExpaProfile>(Expa.ImportCsv<DstsExpaProfile>(source), target)),
        "thl" => new ExpaProfileRunner(
            static (source, target) => Expa.ExportCsv(Expa.Read<ThlExpaProfile>(source), target),
            static (source, target) => Expa.Write<ThlExpaProfile>(Expa.ImportCsv<ThlExpaProfile>(source), target)),
        _ => throw new ArgumentException($"Unknown EXPA profile '{value}'.")
    };

    private static MdbProfileRunner ParseMdbProfile(string value) => value.ToLowerInvariant() switch
    {
        "dscs" => new MdbProfileRunner(
            static (source, target) => Mdb1<DscsMdbProfile>.Open(source).Extract(target),
            static (source, file, target) => WriteOutputFile(target, Mdb1<DscsMdbProfile>.Open(source).ReadFileData(file)),
            static (source, target, mode) => Mdb1<DscsMdbProfile>.Create().AddFolder(source).Write(target, mode),
            static (archive, source, file, mode) => Mdb1<DscsMdbProfile>.AddFileStreaming(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1<DscsMdbProfile>.AddFolderStreaming(archive, source, root, mode),
            static (archive, source, file, mode) => Mdb1Benchmarks.BenchmarkAdd<DscsMdbProfile>(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1Benchmarks.BenchmarkAddFolder<DscsMdbProfile>(archive, source, root, mode)),
        "dscs-nocrypt" => new MdbProfileRunner(
            static (source, target) => Mdb1<DscsNoCryptMdbProfile>.Open(source).Extract(target),
            static (source, file, target) => WriteOutputFile(target, Mdb1<DscsNoCryptMdbProfile>.Open(source).ReadFileData(file)),
            static (source, target, mode) => Mdb1<DscsNoCryptMdbProfile>.Create().AddFolder(source).Write(target, mode),
            static (archive, source, file, mode) => Mdb1<DscsNoCryptMdbProfile>.AddFileStreaming(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1<DscsNoCryptMdbProfile>.AddFolderStreaming(archive, source, root, mode),
            static (archive, source, file, mode) => Mdb1Benchmarks.BenchmarkAdd<DscsNoCryptMdbProfile>(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1Benchmarks.BenchmarkAddFolder<DscsNoCryptMdbProfile>(archive, source, root, mode)),
        "dsts" => new MdbProfileRunner(
            static (source, target) => Mdb1<DstsMdbProfile>.Open(source).Extract(target),
            static (source, file, target) => WriteOutputFile(target, Mdb1<DstsMdbProfile>.Open(source).ReadFileData(file)),
            static (source, target, mode) => Mdb1<DstsMdbProfile>.Create().AddFolder(source).Write(target, mode),
            static (archive, source, file, mode) => Mdb1<DstsMdbProfile>.AddFileStreaming(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1<DstsMdbProfile>.AddFolderStreaming(archive, source, root, mode),
            static (archive, source, file, mode) => Mdb1Benchmarks.BenchmarkAdd<DstsMdbProfile>(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1Benchmarks.BenchmarkAddFolder<DstsMdbProfile>(archive, source, root, mode)),
        "thl" => new MdbProfileRunner(
            static (source, target) => Mdb1<ThlMdbProfile>.Open(source).Extract(target),
            static (source, file, target) => WriteOutputFile(target, Mdb1<ThlMdbProfile>.Open(source).ReadFileData(file)),
            static (source, target, mode) => Mdb1<ThlMdbProfile>.Create().AddFolder(source).Write(target, mode),
            static (archive, source, file, mode) => Mdb1<ThlMdbProfile>.AddFileStreaming(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1<ThlMdbProfile>.AddFolderStreaming(archive, source, root, mode),
            static (archive, source, file, mode) => Mdb1Benchmarks.BenchmarkAdd<ThlMdbProfile>(archive, source, file, mode),
            static (archive, source, root, mode) => Mdb1Benchmarks.BenchmarkAddFolder<ThlMdbProfile>(archive, source, root, mode)),
        _ => throw new ArgumentException($"Unknown MDB1 profile '{value}'.")
    };

    private static CompressMode ParseCompressMode(string value) => value.ToLowerInvariant() switch
    {
        "none" => CompressMode.None,
        "normal" => CompressMode.Normal,
        "advanced" => CompressMode.Advanced,
        _ => throw new ArgumentException($"Unknown compress mode '{value}'.")
    };

    private static (CompressMode Mode, int ValueIndex) ParseOptionalCompressMode(string[] args, int index)
    {
        if (args.Length > index && TryParseCompressMode(args[index], out var mode))
        {
            return (mode, index + 1);
        }

        return (CompressMode.Normal, index);
    }

    private static bool TryParseCompressMode(string value, out CompressMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "none":
                mode = CompressMode.None;
                return true;
            case "normal":
                mode = CompressMode.Normal;
                return true;
            case "advanced":
                mode = CompressMode.Advanced;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static void RequireLength(string[] args, int minLength, string usage)
    {
        if (args.Length < minLength)
        {
            throw new ArgumentException($"Usage: {usage}");
        }
    }

    private static void WriteOutputFile(string target, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(target, data);
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MVGLTools CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  afs2 extract <source> <target>");
        Console.WriteLine("  afs2 pack <source> <target>");
        Console.WriteLine("  save decrypt <source> <target>");
        Console.WriteLine("  save encrypt <source> <target>");
        Console.WriteLine("  expa export-csv <dscs|dsts|thl> <source> <target>");
        Console.WriteLine("  expa import-csv <dscs|dsts|thl> <source> <target>");
        Console.WriteLine("  mdb1 extract <dscs|dscs-nocrypt|dsts|thl> <source> <target>");
        Console.WriteLine("  mdb1 extract-file <dscs|dscs-nocrypt|dsts|thl> <source> <file-in-archive> <target>");
        Console.WriteLine("  mdb1 pack <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <source> <target>");
        Console.WriteLine("  mdb1 add-file-stream <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <archive> <source-file> [file-in-archive]");
        Console.WriteLine("  mdb1 add-folder-stream <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <archive> <source-folder> [archive-root]");
        Console.WriteLine("  mdb1 benchmark-add <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <archive> <source-file> [file-in-archive]");
        Console.WriteLine("  mdb1 benchmark-folder-add <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <archive> <source-folder> [archive-root]");
        Console.WriteLine("    Default compress mode: normal");
    }

    private readonly record struct ExpaProfileRunner(Action<string, string> Read, Action<string, string> Write);
    private readonly record struct MdbProfileRunner(
        Action<string, string> Extract,
        Action<string, string, string> ExtractSingle,
        Action<string, string, CompressMode> Pack,
        Action<string, string, string?, CompressMode> AddFileStreaming,
        Action<string, string, string?, CompressMode> AddFolderStreaming,
        Action<string, string, string?, CompressMode> BenchmarkAdd,
        Action<string, string, string?, CompressMode> BenchmarkAddFolder);
}
