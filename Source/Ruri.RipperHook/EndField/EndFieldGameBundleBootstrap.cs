using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.EndField;

internal static class EndFieldGameBundleBootstrap
{
    public static void PreInitialize(GameBundle bundle, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
    {
        List<string> basePaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? gameDataDirectory = DetectGameDataDirectory(basePaths);
        if (gameDataDirectory is null)
        {
            LoadFilesAndDependencies(basePaths, fileStack, fileSystem, dependencyProvider);
            return;
        }

        List<EndFieldIndexLoader.IndexFile> indexFiles = EndFieldIndexLoader.LoadAll(gameDataDirectory);
        List<EndFieldIndexDecoder.DecodedIndex> decodedIndexes = EndFieldIndexDecoder.DecodeAll(indexFiles);
        EndFieldVfsManifest manifest = EndFieldVfsManifest.Build(gameDataDirectory, decodedIndexes);
        IReadOnlyList<string> vfsChunkPaths = EndFieldVfsResolver.ResolveChunkInputs(manifest);

        Logger.Info(LogCategory.Import, $"[EndField] game-data={gameDataDirectory}");
        Logger.Info(LogCategory.Import, $"[EndField] indexes={indexFiles.Count} decoded={decodedIndexes.Count} buckets={manifest.BucketCount} chunks={manifest.ChunkCount} blc={manifest.BlockInfoCount}");
        EndFieldNativeConsumerProbe.LogSummary(gameDataDirectory);
        EndFieldVfsDiagnostics.LogSummary(manifest);
        EndFieldVfsIndexProbe.LogSummary(manifest);

        HashSet<string> expandedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in basePaths)
        {
            expandedPaths.Add(path);
        }
        foreach (string path in vfsChunkPaths)
        {
            expandedPaths.Add(path);
        }

        Logger.Info(LogCategory.Import, $"[EndField] adding {vfsChunkPaths.Count} VFS chunk input(s) on top of {basePaths.Count} base path(s)");
        EndFieldLoadStats stats = new(vfsChunkPaths);
        LoadFilesAndDependencies(expandedPaths, fileStack, fileSystem, dependencyProvider, stats);
        Logger.Info(LogCategory.Import,
            $"[EndField] VFS classification: serialized={stats.SerializedFiles} container={stats.Containers} resource={stats.Resources} failed={stats.Failed}");
    }

    private static string? DetectGameDataDirectory(IEnumerable<string> paths)
    {
        foreach (string rawPath in paths)
        {
            string? candidate = File.Exists(rawPath) ? Path.GetDirectoryName(rawPath) : rawPath;
            while (!string.IsNullOrWhiteSpace(candidate))
            {
                if (Directory.Exists(Path.Combine(candidate, "StreamingAssets")) &&
                    (Directory.Exists(Path.Combine(candidate, "Persistent")) || Directory.Exists(Path.Combine(candidate, "StreamingAssets", "VFS"))))
                {
                    return candidate;
                }

                DirectoryInfo? parent = Directory.GetParent(candidate);
                candidate = parent?.FullName;
            }
        }
        return null;
    }

    private static void LoadFilesAndDependencies(
        IEnumerable<string> paths,
        List<FileBase> destination,
        FileSystem fileSystem,
        IDependencyProvider? dependencyProvider,
        EndFieldLoadStats? stats = null)
    {
        HashSet<string> serializedFileNames = new(StringComparer.OrdinalIgnoreCase);
        List<FileBase> localFiles = new();

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            FileBase file;
            try
            {
                file = SchemeReader.LoadFile(path, fileSystem);
                file.ReadContentsRecursively();
            }
            catch (Exception ex)
            {
                file = new FailedFile
                {
                    Name = fileSystem.Path.GetFileName(path),
                    FilePath = path,
                    StackTrace = ex.ToString(),
                };
            }

            while (file is CompressedFile compressedFile)
            {
                file = compressedFile.UncompressedFile;
            }

            if (file is ResourceFile or FailedFile)
            {
                localFiles.Add(file);
                stats?.Track(path, file);
            }
            else if (file is SerializedFile serializedFile)
            {
                localFiles.Add(file);
                serializedFileNames.Add(serializedFile.NameFixed);
                stats?.Track(path, file);
            }
            else if (file is FileContainer container)
            {
                localFiles.Add(file);
                foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
                {
                    serializedFileNames.Add(serializedFileInContainer.NameFixed);
                }
                stats?.Track(path, file);
            }
        }

        for (int i = 0; i < localFiles.Count; i++)
        {
            FileBase file = localFiles[i];
            if (file is SerializedFile serializedFile)
            {
                LoadDependencies(serializedFile, localFiles, serializedFileNames, dependencyProvider);
            }
            else if (file is FileContainer container)
            {
                foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
                {
                    LoadDependencies(serializedFileInContainer, localFiles, serializedFileNames, dependencyProvider);
                }
            }
        }

        destination.AddRange(localFiles);
    }

    private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
    {
        foreach (FileIdentifier fileIdentifier in serializedFile.Dependencies)
        {
            string name = fileIdentifier.GetFilePath();
            if (serializedFileNames.Add(name) && dependencyProvider?.FindDependency(fileIdentifier) is { } dependency)
            {
                files.Add(dependency);
            }
        }
    }

    private sealed class EndFieldLoadStats
    {
        private readonly HashSet<string> _vfsChunkPaths;

        public int SerializedFiles { get; private set; }
        public int Containers { get; private set; }
        public int Resources { get; private set; }
        public int Failed { get; private set; }

        public EndFieldLoadStats(IEnumerable<string> vfsChunkPaths)
        {
            _vfsChunkPaths = new HashSet<string>(vfsChunkPaths, StringComparer.OrdinalIgnoreCase);
        }

        public void Track(string path, FileBase file)
        {
            if (!_vfsChunkPaths.Contains(path))
            {
                return;
            }

            switch (file)
            {
                case SerializedFile:
                    SerializedFiles++;
                    break;
                case FileContainer:
                    Containers++;
                    break;
                case FailedFile:
                    Failed++;
                    break;
                default:
                    Resources++;
                    break;
            }
        }
    }
}
