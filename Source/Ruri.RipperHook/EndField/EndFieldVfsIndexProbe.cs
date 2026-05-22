using AssetRipper.Import.Logging;
using System.Text;

namespace Ruri.RipperHook.EndField;

internal static class EndFieldVfsIndexProbe
{
    private const int SampleScanLength = 8 * 1024 * 1024;
    private const int MaxSampleBuckets = 3;
    private const int MaxSampleChunksPerBucket = 2;

    private static readonly string[] PreferredReleaseBuckets =
    [
        "7064D8E2",
        "0CE8FA57",
        "55FC21C6",
    ];

    private static readonly byte[] UnityFsSignature = Encoding.ASCII.GetBytes("UnityFS");
    private static readonly byte[] CabSignature = Encoding.ASCII.GetBytes("CAB-");
    private static readonly byte[] ArchiveCabSignature = Encoding.ASCII.GetBytes("archive:/CAB-");
    private static readonly byte[] UnityVersionSignature = Encoding.ASCII.GetBytes("2021.3.34f5");
    private static readonly byte[] AssetBundleExtension = Encoding.ASCII.GetBytes(".ab");
    private static readonly byte[] ResourceExtension = Encoding.ASCII.GetBytes(".resS");
    private static readonly byte[] MainPathSignature = Encoding.ASCII.GetBytes("main/");

    public static void LogSummary(EndFieldVfsManifest manifest)
    {
        IndexEvidence[] indexEvidence = manifest.DecodedIndexes
            .Select(index => ProbeIndex(index, manifest))
            .ToArray();
        foreach (IndexEvidence evidence in indexEvidence)
        {
            Logger.Info(LogCategory.Import,
                $"[EndField] index probe: {evidence.Label} bytes={evidence.Length} entropy={evidence.Entropy:F2} printableRuns={evidence.PrintableRuns} bucketHits={evidence.BucketHits} chunkHits={evidence.ChunkHits} pathHints={evidence.PathHints} head={evidence.HeadHex}");
        }

        IReadOnlyList<BucketProbe> buckets = SelectBuckets(manifest);
        foreach (BucketProbe bucket in buckets)
        {
            BlcEvidence? blc = ProbeBlc(bucket.BlockInfo, bucket.Chunks);
            if (blc is null)
            {
                Logger.Info(LogCategory.Import,
                    $"[EndField] blc probe: bucket={bucket.Name} missing chunks={bucket.Chunks.Count}");
            }
            else
            {
                Logger.Info(LogCategory.Import,
                    $"[EndField] blc probe: bucket={bucket.Name} bytes={blc.Length} version={blc.Version} entropy={blc.Entropy:F2} chunkHits={blc.ChunkHits} printableRuns={blc.PrintableRuns} head={blc.HeadHex}");
            }

            foreach (ChunkEvidence chunk in bucket.Chunks
                .OrderByDescending(static chunk => new FileInfo(chunk.FullPath).Length)
                .Take(MaxSampleChunksPerBucket)
                .Select(ProbeChunk))
            {
                Logger.Info(LogCategory.Import,
                    $"[EndField] chunk probe: {chunk.RelativePath} size={chunk.Length} unityFS={chunk.UnityFsOffset} cab={chunk.CabOffset} archiveCab={chunk.ArchiveCabOffset} unityVersion={chunk.UnityVersionOffset} ab={chunk.AssetBundleOffset} resS={chunk.ResourceOffset} main={chunk.MainPathOffset}");
            }
        }

        bool metadataLinked = indexEvidence.Any(static evidence => evidence.BucketHits > 0 || evidence.ChunkHits > 0)
            || buckets.Any(static bucket => ProbeBlc(bucket.BlockInfo, bucket.Chunks)?.ChunkHits > 0);
        Logger.Info(LogCategory.Import,
            $"[EndField] metadata chain: linked={metadataLinked} reason={(metadataLinked ? "metadata-name-match" : "index-blc-not-plain-mapped")}");
    }

    private static IndexEvidence ProbeIndex(EndFieldIndexDecoder.DecodedIndex index, EndFieldVfsManifest manifest)
    {
        byte[] bytes = index.RawBytes;
        int bucketHits = CountNeedleHits(bytes, manifest.Files
            .Select(static file => GetBucket(file.RelativePath))
            .Where(static bucket => bucket.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        int chunkHits = CountNeedleHits(bytes, manifest.Files
            .Where(static file => file.IsChunk)
            .Select(static file => Path.GetFileNameWithoutExtension(file.RelativePath)));
        int pathHints = CountNeedleHits(bytes, ["VFS", "CAB", "main/", ".ab", ".resS", "archive:/CAB-"]);

        return new IndexEvidence(
            $"{index.Layer}/{index.Kind}",
            bytes.Length,
            CalculateEntropy(bytes),
            CountPrintableRuns(bytes, 6),
            bucketHits,
            chunkHits,
            pathHints,
            ToHex(bytes.AsSpan(0, Math.Min(8, bytes.Length))));
    }

    private static BlcEvidence? ProbeBlc(EndFieldVfsManifest.VfsFile? blockInfo, IReadOnlyList<EndFieldVfsManifest.VfsFile> chunks)
    {
        if (blockInfo is null || !File.Exists(blockInfo.FullPath))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(blockInfo.FullPath);
        int version = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : -1;
        int chunkHits = CountNeedleHits(bytes, chunks.Select(static file => Path.GetFileNameWithoutExtension(file.RelativePath)));
        return new BlcEvidence(
            bytes.Length,
            version,
            CalculateEntropy(bytes),
            chunkHits,
            CountPrintableRuns(bytes, 6),
            ToHex(bytes.AsSpan(0, Math.Min(8, bytes.Length))));
    }

    private static ChunkEvidence ProbeChunk(EndFieldVfsManifest.VfsFile chunk)
    {
        byte[] bytes = ReadPrefix(chunk.FullPath, SampleScanLength);
        return new ChunkEvidence(
            chunk.RelativePath,
            new FileInfo(chunk.FullPath).Length,
            IndexOf(bytes, UnityFsSignature),
            IndexOf(bytes, CabSignature),
            IndexOf(bytes, ArchiveCabSignature),
            IndexOf(bytes, UnityVersionSignature),
            IndexOf(bytes, AssetBundleExtension),
            IndexOf(bytes, ResourceExtension),
            IndexOf(bytes, MainPathSignature));
    }

    private static IReadOnlyList<BucketProbe> SelectBuckets(EndFieldVfsManifest manifest)
    {
        Dictionary<string, BucketProbeBuilder> builders = new(StringComparer.OrdinalIgnoreCase);
        foreach (EndFieldVfsManifest.VfsFile file in manifest.Files)
        {
            string bucket = GetBucket(file.RelativePath);
            if (bucket.Length == 0)
            {
                continue;
            }

            if (!builders.TryGetValue(bucket, out BucketProbeBuilder? builder))
            {
                builder = new BucketProbeBuilder(bucket);
                builders.Add(bucket, builder);
            }

            if (file.IsBlockInfo)
            {
                builder.BlockInfo = file;
            }
            else if (file.IsChunk)
            {
                builder.Chunks.Add(file);
            }
        }

        List<BucketProbe> result = new();
        foreach (string preferred in PreferredReleaseBuckets)
        {
            if (builders.TryGetValue(preferred, out BucketProbeBuilder? builder))
            {
                result.Add(builder.Build());
            }
        }

        foreach (BucketProbeBuilder builder in builders.Values
            .OrderByDescending(static builder => builder.Chunks.Sum(static chunk => new FileInfo(chunk.FullPath).Length))
            .ThenBy(static builder => builder.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Any(bucket => bucket.Name.Equals(builder.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(builder.Build());
            if (result.Count >= MaxSampleBuckets)
            {
                break;
            }
        }

        return result;
    }

    private static int CountNeedleHits(byte[] haystack, IEnumerable<string> needles)
    {
        int hits = 0;
        foreach (string needle in needles)
        {
            if (needle.Length == 0)
            {
                continue;
            }

            byte[] ascii = Encoding.ASCII.GetBytes(needle);
            if (IndexOf(haystack, ascii) >= 0)
            {
                hits++;
            }
        }

        return hits;
    }

    private static int CountPrintableRuns(ReadOnlySpan<byte> bytes, int minimumLength)
    {
        int runs = 0;
        int current = 0;
        foreach (byte value in bytes)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                current++;
                continue;
            }

            if (current >= minimumLength)
            {
                runs++;
            }
            current = 0;
        }

        return current >= minimumLength ? runs + 1 : runs;
    }

    private static double CalculateEntropy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (byte value in bytes)
        {
            counts[value]++;
        }

        double entropy = 0;
        foreach (int count in counts)
        {
            if (count == 0)
            {
                continue;
            }

            double probability = (double)count / bytes.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static byte[] ReadPrefix(string path, int maxLength)
    {
        using FileStream stream = File.OpenRead(path);
        int length = (int)Math.Min(stream.Length, maxLength);
        byte[] buffer = new byte[length];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read == buffer.Length)
        {
            return buffer;
        }

        Array.Resize(ref buffer, read);
        return buffer;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty || data.Length < pattern.Length)
        {
            return -1;
        }

        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            if (data.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetBucket(string relativePath)
    {
        int separator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return string.Join(" ", bytes.ToArray().Select(static value => value.ToString("X2")));
    }

    private sealed record IndexEvidence(
        string Label,
        int Length,
        double Entropy,
        int PrintableRuns,
        int BucketHits,
        int ChunkHits,
        int PathHints,
        string HeadHex);

    private sealed record BlcEvidence(
        int Length,
        int Version,
        double Entropy,
        int ChunkHits,
        int PrintableRuns,
        string HeadHex);

    private sealed record ChunkEvidence(
        string RelativePath,
        long Length,
        int UnityFsOffset,
        int CabOffset,
        int ArchiveCabOffset,
        int UnityVersionOffset,
        int AssetBundleOffset,
        int ResourceOffset,
        int MainPathOffset);

    private sealed record BucketProbe(
        string Name,
        EndFieldVfsManifest.VfsFile? BlockInfo,
        IReadOnlyList<EndFieldVfsManifest.VfsFile> Chunks);

    private sealed class BucketProbeBuilder
    {
        public string Name { get; }
        public EndFieldVfsManifest.VfsFile? BlockInfo { get; set; }
        public List<EndFieldVfsManifest.VfsFile> Chunks { get; } = new();

        public BucketProbeBuilder(string name)
        {
            Name = name;
        }

        public BucketProbe Build()
        {
            return new BucketProbe(Name, BlockInfo, Chunks
                .OrderBy(static chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }
}
