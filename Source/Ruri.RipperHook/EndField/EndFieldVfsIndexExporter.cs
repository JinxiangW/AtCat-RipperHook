using System.Text.Json;
using Ruri.RipperHook.Endfield;

namespace Ruri.RipperHook.EndField;

public static class EndFieldVfsIndexExporter
{
    public static string Export(string gameRootOrDataDirectory, string? outputRoot = null)
    {
        string dataDirectory = ResolveDataDirectory(gameRootOrDataDirectory);
        string outRoot = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(Environment.CurrentDirectory, "out", "endfield_vfs_index")
            : Path.GetFullPath(outputRoot);
        string indexRoot = Path.Combine(outRoot, "index");
        Directory.CreateDirectory(indexRoot);

        string logicalPath = Path.Combine(indexRoot, "logical_files.json");
        string abPath = Path.Combine(indexRoot, "ab_files.json");

        int sequence = 0;
        int logicalCount = 0;
        int abCount = 0;
        int parseErrors = 0;
        Dictionary<string, int> extensionCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> sourceRootCounts = new(StringComparer.OrdinalIgnoreCase);

        using FileStream logicalStream = File.Create(logicalPath);
        using FileStream abStream = File.Create(abPath);
        using Utf8JsonWriter logicalWriter = CreateWriter(logicalStream);
        using Utf8JsonWriter abWriter = CreateWriter(abStream);
        logicalWriter.WriteStartArray();
        abWriter.WriteStartArray();

        foreach ((string sourceRoot, string vfsRoot) in EnumerateVfsRoots(dataDirectory))
        {
            foreach (string blockInfoPath in Directory.EnumerateFiles(vfsRoot, "*.blc", SearchOption.AllDirectories).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!EndField_0_8_25_Vfs.TryParseBlockInfo(blockInfoPath, out EndFieldVfsBlock block, out string error))
                {
                    parseErrors++;
                    Console.Error.WriteLine($"[EndField] vfs-index parse-error source={sourceRoot} file={Path.GetFileName(blockInfoPath)} reason={error}");
                    continue;
                }

                foreach (EndFieldVfsChunk chunk in block.Chunks)
                {
                    string chunkPath = Path.Combine(Path.GetDirectoryName(block.BlockInfoPath) ?? string.Empty, chunk.Md5Name + ".chk");
                    bool chunkExists = File.Exists(chunkPath);

                    foreach (EndFieldVfsFile file in chunk.Files)
                    {
                        sequence++;
                        logicalCount++;
                        string extension = Path.GetExtension(file.FileName);
                        extensionCounts[extension] = extensionCounts.GetValueOrDefault(extension) + 1;
                        sourceRootCounts[sourceRoot] = sourceRootCounts.GetValueOrDefault(sourceRoot) + 1;

                        WriteRecord(logicalWriter, sequence, sourceRoot, block, chunk, chunkPath, chunkExists, file, extension);
                        if (extension.Equals(".ab", StringComparison.OrdinalIgnoreCase))
                        {
                            abCount++;
                            WriteRecord(abWriter, sequence, sourceRoot, block, chunk, chunkPath, chunkExists, file, extension);
                        }
                    }
                }
            }
        }

        logicalWriter.WriteEndArray();
        abWriter.WriteEndArray();

        WriteSummary(outRoot, dataDirectory, logicalCount, abCount, parseErrors, sourceRootCounts, extensionCounts);
        Console.Error.WriteLine($"[EndField] vfs-index out={outRoot} logical={logicalCount} ab={abCount} parseErrors={parseErrors}");
        return outRoot;
    }

    private static Utf8JsonWriter CreateWriter(Stream stream)
    {
        return new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
    }

    private static void WriteRecord(
        Utf8JsonWriter writer,
        int sequence,
        string sourceRoot,
        EndFieldVfsBlock block,
        EndFieldVfsChunk chunk,
        string chunkPath,
        bool chunkExists,
        EndFieldVfsFile file,
        string extension)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Sequence", sequence);
        writer.WriteString("SourceRoot", sourceRoot);
        writer.WriteString("BlockInfoPath", block.BlockInfoPath);
        writer.WriteString("GroupName", block.GroupName);
        writer.WriteNumber("CodeVersion", block.CodeVersion);
        writer.WriteNumber("Version", block.Version);
        writer.WriteString("ChunkMd5Name", chunk.Md5Name);
        writer.WriteString("ChunkContentMd5", chunk.ContentMd5);
        writer.WriteNumber("ChunkLength", chunk.Length);
        writer.WriteString("ChunkPath", chunkPath);
        writer.WriteBoolean("ChunkExists", chunkExists);
        writer.WriteString("LogicalPath", file.FileName);
        writer.WriteString("Extension", extension);
        writer.WriteString("FileNameHash", file.FileNameHash.ToString("X16"));
        writer.WriteString("FileChunkMd5Name", file.FileChunkMd5Name);
        writer.WriteString("FileDataMd5", file.FileDataMd5);
        writer.WriteNumber("Offset", file.Offset);
        writer.WriteNumber("Length", file.Length);
        writer.WriteNumber("BlockType", file.BlockType);
        writer.WriteBoolean("UseEncrypt", file.UseEncrypt);
        writer.WriteString("IvSeed", file.UseEncrypt ? file.IvSeed.ToString("X16") : string.Empty);
        writer.WriteNumber("Reserved", file.Reserved);
        writer.WriteEndObject();
    }

    private static void WriteSummary(
        string outputRoot,
        string dataDirectory,
        int logicalCount,
        int abCount,
        int parseErrors,
        Dictionary<string, int> sourceRootCounts,
        Dictionary<string, int> extensionCounts)
    {
        string reportsRoot = Path.Combine(outputRoot, "reports");
        Directory.CreateDirectory(reportsRoot);
        using StreamWriter writer = new(Path.Combine(reportsRoot, "scan_summary.md"));
        writer.WriteLine("# EndField VFS Scan Summary");
        writer.WriteLine();
        writer.WriteLine($"Data directory: `{dataDirectory}`");
        writer.WriteLine($"Logical files: {logicalCount}");
        writer.WriteLine($"Logical .ab files: {abCount}");
        writer.WriteLine($"Parse errors: {parseErrors}");
        writer.WriteLine();
        writer.WriteLine("## Source roots");
        foreach ((string key, int value) in sourceRootCounts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine($"- {key}: {value}");
        }
        writer.WriteLine();
        writer.WriteLine("## Extension counts");
        foreach ((string key, int value) in extensionCounts.OrderByDescending(static item => item.Value))
        {
            writer.WriteLine($"- {(string.IsNullOrEmpty(key) ? "(none)" : key)}: {value}");
        }
    }

    private static IEnumerable<(string SourceRoot, string VfsRoot)> EnumerateVfsRoots(string dataDirectory)
    {
        string streaming = Path.Combine(dataDirectory, "StreamingAssets", "VFS");
        if (Directory.Exists(streaming))
        {
            yield return ("StreamingAssets", streaming);
        }

        string persistent = Path.Combine(dataDirectory, "Persistent", "VFS");
        if (Directory.Exists(persistent))
        {
            yield return ("Persistent", persistent);
        }
    }

    private static string ResolveDataDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(Path.Combine(fullPath, "StreamingAssets")))
        {
            return fullPath;
        }

        foreach (string candidate in Directory.EnumerateDirectories(fullPath, "*_Data", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(Path.Combine(candidate, "StreamingAssets")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Cannot locate Endfield data directory from: {path}");
    }
}
