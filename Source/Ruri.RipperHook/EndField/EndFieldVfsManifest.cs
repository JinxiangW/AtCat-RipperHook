namespace Ruri.RipperHook.EndField;

internal sealed class EndFieldVfsManifest
{
    internal sealed record VfsFile(string RelativePath, string FullPath, bool IsChunk, bool IsBlockInfo, EndFieldIndexLoader.IndexLayer Layer);

    public required string GameDataDirectory { get; init; }
    public required string? StreamingAssetsVfsRoot { get; init; }
    public required string? PersistentVfsRoot { get; init; }
    public required IReadOnlyList<EndFieldIndexDecoder.DecodedIndex> DecodedIndexes { get; init; }
    public required IReadOnlyList<VfsFile> Files { get; init; }

    public int BucketCount => Files
        .Select(file => Path.GetDirectoryName(file.RelativePath) ?? string.Empty)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int ChunkCount => Files.Count(file => file.IsChunk);
    public int BlockInfoCount => Files.Count(file => file.IsBlockInfo);

    public static EndFieldVfsManifest Build(string gameDataDirectory, IReadOnlyList<EndFieldIndexDecoder.DecodedIndex> decodedIndexes)
    {
        string? streamingRoot = Directory.Exists(Path.Combine(gameDataDirectory, "StreamingAssets", "VFS"))
            ? Path.Combine(gameDataDirectory, "StreamingAssets", "VFS")
            : null;
        string? persistentRoot = Directory.Exists(Path.Combine(gameDataDirectory, "Persistent", "VFS"))
            ? Path.Combine(gameDataDirectory, "Persistent", "VFS")
            : null;

        Dictionary<string, VfsFile> merged = new(StringComparer.OrdinalIgnoreCase);
        AddLayerFiles(merged, streamingRoot, EndFieldIndexLoader.IndexLayer.StreamingAssets);
        AddLayerFiles(merged, persistentRoot, EndFieldIndexLoader.IndexLayer.Persistent);

        return new EndFieldVfsManifest
        {
            GameDataDirectory = gameDataDirectory,
            StreamingAssetsVfsRoot = streamingRoot,
            PersistentVfsRoot = persistentRoot,
            DecodedIndexes = decodedIndexes,
            Files = merged.Values.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static void AddLayerFiles(Dictionary<string, VfsFile> merged, string? root, EndFieldIndexLoader.IndexLayer layer)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string extension = Path.GetExtension(file);
            bool isChunk = extension.Equals(".chk", StringComparison.OrdinalIgnoreCase);
            bool isBlockInfo = extension.Equals(".blc", StringComparison.OrdinalIgnoreCase);
            if (!isChunk && !isBlockInfo)
            {
                continue;
            }

            merged[relative] = new VfsFile(relative, file, isChunk, isBlockInfo, layer);
        }
    }
}
