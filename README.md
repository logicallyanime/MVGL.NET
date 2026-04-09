# MVGL.NET

MVGL.NET is a crude .NET command-line tool and library for working with several Media.Vision game formats used by:

- Digimon Story: Cyber Sleuth (DSCS)
- Digimon Story: Time Stranger (DSTS)
- The Hundred Line -Last Defense Academy- (THL)

It can unpack and repack `MDB1` archives, extract and create `AFS2` archives, convert `EXPA` table data to and from CSV, and encrypt or decrypt DSCS save files.

## Credit and provenance

This project is based on the original [**MVGLTools / DSCSTools**](https://github.com/SydMontague/MVGLTools) work by [**SydMontague**](https://github.com/SydMontague).

The original project documentation and license have been kept in this repository as:

- [README_old.md](https://github.com/SydMontague/MVGLTools/README.md)
- [LICENSE_old](https://github.com/SydMontague/MVGLTools/LICENSE)

This README is an updated adaptation of that older documentation for the current C#/.NET version in this repository. The original author deserves the core format research and the original implementation credit.

## What changed in this version

Compared to the old README and older CLI:

- this repository is a **C#/.NET rewrite** rather than the original native implementation
- the CLI now uses **subcommands** instead of `--game=... --mode=...`
- `MDB1` handling is profile-based via `dscs`, `dscs-nocrypt`, `dsts`, and `thl`
- table conversion is exposed as **`expa`** commands
- the currently implemented command set is narrower than the old README: some older modes described there are not exposed by this build
- save encryption and decryption are currently exposed as direct `save` commands

If you are familiar with the old syntax, check the usage section below before assuming older commands still apply.

## Current features

- Unpack `MDB1` / `.mvgl` archives
- Extract a single file from an `MDB1` archive
- Repack / create `MDB1` archives
  - `normal` compression
  - `none` compression
  - `advanced` compression with data deduplication
- Unpack and repack `AFS2` archives
- Export `EXPA` tables to CSV
- Import CSV back into `EXPA`
- Encrypt and decrypt DSCS PC save files
- Use the library APIs directly from .NET code

## Requirements

- .NET 8 SDK

This project currently targets `net8.0`.

## Build

From the project folder, build with the .NET SDK:

- `dotnet build .\MVGLTools.csproj`

You can run the tool either through `dotnet run` or by invoking the built executable.

## Usage

General form:

- `MVGLTools <command> ...`

When running through the SDK during development:

- `dotnet run -- <command> ...`

### Commands

#### MDB1

- `mdb1 extract <dscs|dscs-nocrypt|dsts|thl> <source> <target>`
- `mdb1 extract-file <dscs|dscs-nocrypt|dsts|thl> <source> <file-in-archive> <target>`
- `mdb1 pack <dscs|dscs-nocrypt|dsts|thl> [none|normal|advanced] <source> <target>`

Notes:

- default compression mode is `normal`
- use `dscs` for encrypted DSCS archives
- use `dscs-nocrypt` for already decrypted DSCS assets / console-style data

Examples:

- `dotnet run -- mdb1 extract dscs game.mvgl extracted`
- `dotnet run -- mdb1 extract-file dsts data.mvgl rom/chr/hero.bin hero.bin`
- `dotnet run -- mdb1 pack thl advanced unpacked rebuilt.mvgl`

#### EXPA

- `expa export-csv <dscs|dsts|thl> <source> <target>`
- `expa import-csv <dscs|dsts|thl> <source> <target>`

Examples:

- `dotnet run -- expa export-csv dscs field_data.expa csv_out`
- `dotnet run -- expa import-csv dsts csv_out rebuilt.expa`

#### AFS2

- `afs2 extract <source> <target>`
- `afs2 pack <source> <target>`

The extracted audio files are written as `.hca` files.

#### Save files

- `save decrypt <source> <target>`
- `save encrypt <source> <target>`

These commands are intended for **DSCS save files**.

## EXPA structures

For `EXPA` operations, the tool can load external structure definitions from a `structures` folder placed relative to the current working directory.

Current profile folders used by this build are:

- `structures/dscs`
- `structures/dsts`
- `structures/tlh`

Each folder may contain a `structure.json` file that maps file path regex patterns to structure definition files, following the same general idea described in [README_old.md](README_old.md).

Behavior summary:

- for DSCS, external structure files are typically needed
- for DSTS and THL, the file may already contain structure information, but external names can still improve output
- CSV headers also carry type information in this version, so imported CSV can provide structure hints on its own

## Library usage

The project can also be referenced from another .NET project instead of only being used as a CLI.

The public API lives in the `MVGLTools` namespace. Main entry points include:

- `Mdb1<TProfile>`
- `Afs2`
- `Expa`
- `SaveFile`

Examples of available profiles include:

- `DscsMdbProfile`
- `DscsNoCryptMdbProfile`
- `DstsMdbProfile`
- `ThlMdbProfile`
- `DscsExpaProfile`
- `DstsExpaProfile`
- `ThlExpaProfile`

### Referencing the project

If this repository is part of the same solution, add a normal project reference from your own `.csproj`.

This project is also set up so its output type can be overridden. If you specifically want to build it as a library assembly, you can do so by setting `MVGLToolsOutputType=Library` at build time.

Typical example:

- `dotnet build .\MVGLTools.csproj -p:MVGLToolsOutputType=Library`

After that, reference the produced assembly or add a project reference and use the `MVGLTools` namespace in your code.

### API overview

#### `Mdb1<TProfile>`

Most library users will spend most of their time with `Mdb1<TProfile>`.

Useful factory methods:

- `Mdb1<TProfile>.Create()` creates a new in-memory archive
- `Mdb1<TProfile>.Open(path)` / `Read(path)` loads an archive from disk

Useful properties:

- `SourcePath` returns the path the archive was loaded from or last written to
- `Files` returns the archive file list

Useful read methods:

- `Extract(outputFolder)` extracts the full archive to disk
- `ExtractSingleFile(outputPath, archivePath)` extracts one file
- `GetFileData(archivePath)` / `ReadFileData(archivePath)` returns a file as `byte[]`
- `ContainsFile(archivePath)` checks whether a file exists in the archive

Useful write/edit methods:

- `AddFile(sourcePath, archivePath)` adds a disk file into the archive
- `AddFile(archivePath, data)` adds raw bytes into the archive
- `AddFolder(sourceFolder, archiveRoot)` recursively adds a folder tree
- `UpdateFile(sourcePath, archivePath)` replaces an existing archive entry from disk
- `UpdateFile(archivePath, data)` replaces an existing archive entry from bytes
- `RemoveFile(archivePath)` / `DeleteFile(archivePath)` removes an entry
- `Write(target, compressMode)` writes the archive to disk
- `Write(stream, compressMode)` writes to a stream
- `ToStream(compressMode)` builds the archive into a `MemoryStream`

#### `Expa`

Useful static methods:

- `Expa.Read<TProfile>(path)` reads an `EXPA` file into a `TableFile`
- `Expa.ExportCsv(tableFile, targetFolder)` exports tables as CSV
- `Expa.ImportCsv<TProfile>(sourceFolder)` reads CSV back into a `TableFile`
- `Expa.Write<TProfile>(tableFile, targetPath)` writes a rebuilt `EXPA` file

#### `Afs2`

Useful static methods:

- `Afs2.Extract(source, target)` extracts an `AFS2` archive
- `Afs2.Pack(source, target)` builds an `AFS2` archive from a folder

#### `SaveFile`

Useful static methods:

- `SaveFile.Decrypt(source, target)` decrypts a DSCS save file
- `SaveFile.Encrypt(source, target)` encrypts a DSCS save file

### Example: extract an archive

```csharp
using MVGLTools;

var archive = Mdb1<DscsMdbProfile>.Open("DSDBP.steam.mvgl");
archive.Extract("DSDBP_extracted");
```

### Example: create or rebuild an archive

```csharp
using MVGLTools;

Mdb1<DstsMdbProfile>.Create()
  .AddFolder("DSDBP_extracted")
  .Write("DSDBP.steam.mvgl", CompressMode.Advanced);
```

### Example: modify one file in an archive

```csharp
using MVGLTools;

var archive = Mdb1<ThlMdbProfile>.Open("DSDBP.steam.mvgl");
archive.UpdateFile("ui_chara_icon_1819.img", "images/ui_chara_icon_1819.img");
archive.Write("DSDBP.steam.mvgl", CompressMode.Normal);
```

### Example: convert EXPA to and from CSV

```csharp
using MVGLTools;

var tableFile = Expa.Read<DscsExpaProfile>("m00_d02_0501.mbe");
Expa.ExportCsv(tableFile, "m00_d02_0501.csv");

var rebuilt = Expa.ImportCsv<DscsExpaProfile>("m00_d02_0501.csv");
Expa.Write<DscsExpaProfile>(rebuilt, "m00_d02_0501_new.mbe");
```

### Example: work with AFS2 and save files

```csharp
using MVGLTools;

Afs2.Extract("DSDBvo.mgvl", "DSDBvo");
Afs2.Pack("DSDBvo", "DSDBvo.mgvl");

SaveFile.Decrypt("slot_0001.bin", "slot_0001.dec.bin");
SaveFile.Encrypt("slot_0001.dec.bin", "slot_0001.enc.bin");
```

### Notes for library consumers

- `Mdb1<TProfile>` keeps archive contents in memory, so very large archives may require substantial RAM and may take time to fully load.
- `Extract()` writes all files to disk, while `GetFileData()` / `ReadFileData()` let you access a single file as bytes.
- `AddFile()`, `UpdateFile()`, `RemoveFile()`, and `ContainsFile()` support programmatic archive editing workflows.
- `EXPA` structure lookups still depend on the working directory if you rely on external `structures/...` folders. This may be addressed in the future.
- `SaveFile` is currently relevant to DSCS save encryption only.


## Notes

- The older README documents features and workflows from the original project lineage. It remains useful historical reference material, but it does **not** exactly match the current CLI.
- If you are migrating scripts from the old tool, update them to the new subcommand-based syntax.

## Credits

This project builds on format research and prior implementation work by SydMontague and the broader modding community referenced in the original documentation.

Third-party libraries used by this version include:

- `doboz4net` for Doboz compression support
- `K4os.Compression.LZ4` for LZ4 support

For broader historical credits, related tools, and community links, see [README_old.md](README_old.md).

## License

See [LICENSE_old](LICENSE_old) for the preserved upstream license text included with this repository.
