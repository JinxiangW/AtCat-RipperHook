using AssetRipper.Import.Logging;
using System.Text;

namespace Ruri.RipperHook.EndField;

internal static class EndFieldVfsDiagnostics
{
    private const int HeaderLength = 16;
    private const int SignatureLength = 8;
    private const int MaxScanLength = 8 * 1024 * 1024;

    private static readonly byte[] UnityFsSignature = Encoding.ASCII.GetBytes("UnityFS");
    private static readonly byte[] CabSignature = Encoding.ASCII.GetBytes("CAB-");
    private static readonly byte[] ArchiveCabSignature = Encoding.ASCII.GetBytes("archive:/CAB-");
    private static readonly byte[] UnityVersionSignature = Encoding.ASCII.GetBytes("2021.3.34f5");

    public static void LogSummary(EndFieldVfsManifest manifest)
    {
        List<ChunkProbe> probes = manifest.Files
            .Where(static file => file.IsChunk)
            .Select(Probe)
            .ToList();

        long totalBytes = probes.Sum(static probe => probe.Length);
        int directUnityFs = probes.Count(static probe => probe.UnityFsOffset == 0);
        int embeddedUnityFs = probes.Count(static probe => probe.UnityFsOffset > 0);
        int cab = probes.Count(static probe => probe.CabOffset >= 0);
        int archiveCab = probes.Count(static probe => probe.ArchiveCabOffset >= 0);
        int unityVersion = probes.Count(static probe => probe.UnityVersionOffset >= 0);

        Logger.Info(LogCategory.Import,
            $"[EndField] VFS probe: chunks={probes.Count} bytes={totalBytes} scanLimit={MaxScanLength}");
        Logger.Info(LogCategory.Import,
            $"[EndField] VFS signatures: directUnityFS={directUnityFs} embeddedUnityFS={embeddedUnityFs} cab={cab} archiveCab={archiveCab} unityVersion={unityVersion}");

        foreach (var group in probes
            .GroupBy(static probe => probe.SignatureHex, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            ChunkProbe first = group.First();
            Logger.Info(LogCategory.Import,
                $"[EndField] VFS head: count={group.Count()} bytes={group.Sum(static probe => probe.Length)} hex={group.Key} ascii={first.HeaderAscii}");
        }

        foreach (var bucket in probes
            .GroupBy(static probe => probe.Bucket, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new
            {
                Bucket = group.Key,
                Count = group.Count(),
                Bytes = group.Sum(static probe => probe.Length),
                DirectUnityFs = group.Count(static probe => probe.UnityFsOffset == 0),
                EmbeddedUnityFs = group.Count(static probe => probe.UnityFsOffset > 0),
                Cab = group.Count(static probe => probe.CabOffset >= 0),
                ArchiveCab = group.Count(static probe => probe.ArchiveCabOffset >= 0),
                UnityVersion = group.Count(static probe => probe.UnityVersionOffset >= 0),
            })
            .OrderByDescending(static bucket => bucket.Bytes)
            .Take(8))
        {
            Logger.Info(LogCategory.Import,
                $"[EndField] VFS bucket: {bucket.Bucket} chunks={bucket.Count} bytes={bucket.Bytes} directUnityFS={bucket.DirectUnityFs} embeddedUnityFS={bucket.EmbeddedUnityFs} cab={bucket.Cab} archiveCab={bucket.ArchiveCab} unityVersion={bucket.UnityVersion}");
        }

        LogSamples("directUnityFS", probes.Where(static probe => probe.UnityFsOffset == 0));
        LogSamples("embeddedUnityFS", probes.Where(static probe => probe.UnityFsOffset > 0));
        LogSamples("cab", probes.Where(static probe => probe.CabOffset >= 0));
        LogSamples("archiveCab", probes.Where(static probe => probe.ArchiveCabOffset >= 0));
        LogSamples("unityVersion", probes.Where(static probe => probe.UnityVersionOffset >= 0));
    }

    private static void LogSamples(string label, IEnumerable<ChunkProbe> probes)
    {
        foreach (ChunkProbe probe in probes
            .OrderBy(static probe => probe.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            Logger.Info(LogCategory.Import,
                $"[EndField] VFS sample {label}: {probe.RelativePath} size={probe.Length} unityFS={probe.UnityFsOffset} cab={probe.CabOffset} archiveCab={probe.ArchiveCabOffset} unityVersion={probe.UnityVersionOffset}");
        }
    }

    private static ChunkProbe Probe(EndFieldVfsManifest.VfsFile file)
    {
        byte[] buffer = ReadPrefix(file.FullPath);
        string signatureHex = ToHex(buffer.AsSpan(0, Math.Min(SignatureLength, buffer.Length)));
        string headerAscii = ToAscii(buffer.AsSpan(0, Math.Min(HeaderLength, buffer.Length)));

        return new ChunkProbe(
            file.RelativePath,
            GetBucket(file.RelativePath),
            new FileInfo(file.FullPath).Length,
            signatureHex,
            headerAscii,
            IndexOf(buffer, UnityFsSignature),
            IndexOf(buffer, CabSignature),
            IndexOf(buffer, ArchiveCabSignature),
            IndexOf(buffer, UnityVersionSignature));
    }

    private static byte[] ReadPrefix(string path)
    {
        using FileStream stream = File.OpenRead(path);
        int length = (int)Math.Min(stream.Length, MaxScanLength);
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

    private static string ToAscii(ReadOnlySpan<byte> bytes)
    {
        char[] chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            chars[i] = value is >= 0x20 and <= 0x7E ? (char)value : '.';
        }

        return new string(chars);
    }

    private sealed record ChunkProbe(
        string RelativePath,
        string Bucket,
        long Length,
        string SignatureHex,
        string HeaderAscii,
        int UnityFsOffset,
        int CabOffset,
        int ArchiveCabOffset,
        int UnityVersionOffset);
}
