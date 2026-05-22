using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.EndField;

public static class EndFieldAssetLocatorProbe
{
    private const int BufferSize = 1024 * 1024;
    private const int ContextRadius = 4096;
    private const int MetadataRadius = 512 * 1024;
    private const int MaxNearbyStrings = 24;
    private const int MaxMetadataItems = 96;

    private static readonly Regex PropertyNameRegex = new("^_[A-Za-z][A-Za-z0-9_]{3,}$", RegexOptions.Compiled);
    private static readonly Regex KeywordRegex = new("^[A-Z][A-Z0-9_]{4,}$", RegexOptions.Compiled);

    public static IEnumerable<string> Analyze(string gameDataDirectory, string query, int maxMatches)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "asset locator error=empty-query";
            yield break;
        }

        string normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
        List<EndFieldIndexLoader.IndexFile> indexFiles = EndFieldIndexLoader.LoadAll(normalizedGameDataDirectory);
        List<EndFieldIndexDecoder.DecodedIndex> decodedIndexes = EndFieldIndexDecoder.DecodeAll(indexFiles);
        EndFieldVfsManifest manifest = EndFieldVfsManifest.Build(normalizedGameDataDirectory, decodedIndexes);
        List<SearchPattern> patterns = BuildPatterns(query);

        int effectiveMaxMatches = maxMatches <= 0 ? 32 : maxMatches;
        long scannedBytes = 0;
        int scannedChunks = 0;
        int matchCount = 0;

        yield return $"asset locator: gameData={normalizedGameDataDirectory} query={JsonSerializer.Serialize(query)} chunks={manifest.ChunkCount} patterns={patterns.Count} maxMatches={effectiveMaxMatches}";

        foreach (EndFieldVfsManifest.VfsFile file in manifest.Files.Where(static file => file.IsChunk))
        {
            FileInfo info = new(file.FullPath);
            scannedBytes += info.Length;
            scannedChunks++;

            foreach (AssetMatch match in FindMatches(file, info.Length, patterns))
            {
                yield return "asset match " + JsonSerializer.Serialize(match, EndFieldJson.Options);
                matchCount++;
                if (matchCount >= effectiveMaxMatches)
                {
                    yield return $"asset locator summary: scannedChunks={scannedChunks} scannedBytes={scannedBytes} matches={matchCount} stopped=max-matches";
                    yield break;
                }
            }
        }

        yield return $"asset locator summary: scannedChunks={scannedChunks} scannedBytes={scannedBytes} matches={matchCount} stopped=end";
    }

    public static string ExportMetadata(string gameDataDirectory, string query, string outputPath, int maxMatches)
    {
        string normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
        string normalizedOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedOutputPath) ?? Directory.GetCurrentDirectory());

        List<EndFieldIndexLoader.IndexFile> indexFiles = EndFieldIndexLoader.LoadAll(normalizedGameDataDirectory);
        List<EndFieldIndexDecoder.DecodedIndex> decodedIndexes = EndFieldIndexDecoder.DecodeAll(indexFiles);
        EndFieldVfsManifest manifest = EndFieldVfsManifest.Build(normalizedGameDataDirectory, decodedIndexes);
        int effectiveMaxMatches = maxMatches <= 0 ? 32 : maxMatches;

        List<MetadataQueryResult> queryResults = new();
        foreach (string metadataQuery in BuildMetadataQueries(query))
        {
            List<SearchPattern> patterns = BuildPatterns(metadataQuery);
            List<AssetMatch> matches = new();
            foreach (EndFieldVfsManifest.VfsFile file in manifest.Files.Where(static file => file.IsChunk))
            {
                FileInfo info = new(file.FullPath);
                foreach (AssetMatch match in FindMatches(file, info.Length, patterns))
                {
                    matches.Add(match);
                    if (matches.Count >= effectiveMaxMatches)
                    {
                        break;
                    }
                }

                if (matches.Count >= effectiveMaxMatches)
                {
                    break;
                }
            }

            queryResults.Add(new MetadataQueryResult(
                metadataQuery,
                matches,
                matches.Select(BuildMetadataWindow).ToArray()));
        }

        MetadataReport report = new(
            query,
            normalizedGameDataDirectory,
            DateTimeOffset.UtcNow,
            queryResults,
            "This is offline VFS string/metadata extraction, not Unity Material serialized value recovery. Empty material parameters mean the matched chunk is only a path/string table.");

        File.WriteAllText(normalizedOutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
        }), Encoding.UTF8);
        return normalizedOutputPath;
    }

    private static IEnumerable<AssetMatch> FindMatches(EndFieldVfsManifest.VfsFile file, long fileLength, IReadOnlyList<SearchPattern> patterns)
    {
        int maxPatternLength = patterns.Max(static pattern => pattern.Bytes.Length);
        byte[] readBuffer = new byte[BufferSize];
        byte[] carry = [];
        long totalRead = 0;
        HashSet<long> emittedOffsets = new();

        using FileStream stream = File.OpenRead(file.FullPath);
        while (true)
        {
            int read = stream.Read(readBuffer, 0, readBuffer.Length);
            if (read <= 0)
            {
                yield break;
            }

            byte[] combined = new byte[carry.Length + read];
            carry.CopyTo(combined, 0);
            Buffer.BlockCopy(readBuffer, 0, combined, carry.Length, read);
            long combinedStartOffset = totalRead - carry.Length;

            foreach (SearchPattern pattern in patterns)
            {
                int index = 0;
                while (index <= combined.Length - pattern.Bytes.Length)
                {
                    int hit = IndexOf(combined.AsSpan(index), pattern.Bytes);
                    if (hit < 0)
                    {
                        break;
                    }

                    int combinedIndex = index + hit;
                    long absoluteOffset = combinedStartOffset + combinedIndex;
                    index = combinedIndex + 1;

                    if (absoluteOffset + pattern.Bytes.Length <= totalRead)
                    {
                        continue;
                    }

                    if (!emittedOffsets.Add(absoluteOffset))
                    {
                        continue;
                    }

                    yield return BuildMatch(file, fileLength, absoluteOffset, pattern);
                }
            }

            totalRead += read;
            int carryLength = Math.Min(maxPatternLength - 1, combined.Length);
            carry = new byte[carryLength];
            Buffer.BlockCopy(combined, combined.Length - carryLength, carry, 0, carryLength);
        }
    }

    private static AssetMatch BuildMatch(EndFieldVfsManifest.VfsFile file, long fileLength, long offset, SearchPattern pattern)
    {
        long contextStart = Math.Max(0, offset - ContextRadius);
        int contextLength = (int)Math.Min(ContextRadius * 2L, fileLength - contextStart);
        byte[] context = new byte[contextLength];
        using (FileStream stream = File.OpenRead(file.FullPath))
        {
            stream.Position = contextStart;
            int read = stream.Read(context, 0, context.Length);
            if (read < context.Length)
            {
                Array.Resize(ref context, read);
            }
        }

        return new AssetMatch(
            file.RelativePath,
            file.FullPath,
            file.Layer.ToString(),
            fileLength,
            offset,
            $"0x{offset:X}",
            pattern.QueryVariant,
            pattern.EncodingName,
            ExtractMatchedString(context, offset - contextStart, pattern.EncodingName),
            contextStart,
            ExtractNearbyStrings(context));
    }

    private static List<SearchPattern> BuildPatterns(string query)
    {
        HashSet<string> variants = new(StringComparer.Ordinal)
        {
            query,
        };

        if (query.Contains('\\', StringComparison.Ordinal))
        {
            variants.Add(query.Replace('\\', '/'));
        }

        if (query.Contains('/', StringComparison.Ordinal))
        {
            variants.Add(query.Replace('/', '\\'));
        }

        List<SearchPattern> patterns = new();
        foreach (string variant in variants.Where(static variant => variant.Length > 0))
        {
            patterns.Add(new SearchPattern(variant, "utf8", Encoding.UTF8.GetBytes(variant)));
            patterns.Add(new SearchPattern(variant, "utf16le", Encoding.Unicode.GetBytes(variant)));
        }

        return patterns
            .Where(static pattern => pattern.Bytes.Length > 0)
            .GroupBy(static pattern => Convert.ToHexString(pattern.Bytes), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
    }

    private static IReadOnlyList<string> BuildMetadataQueries(string query)
    {
        HashSet<string> queries = new(StringComparer.OrdinalIgnoreCase);
        Add(query);

        string fileName = query.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? query;
        Add(fileName);

        if (query.Contains("DeferredLighting", StringComparison.OrdinalIgnoreCase))
        {
            Add("DeferredLighting");
            Add("DeferredLightingPerLight");
            Add("svc_deferred");
        }

        return queries.ToArray();

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                queries.Add(value);
            }
        }
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

    private static IReadOnlyList<string> ExtractNearbyStrings(ReadOnlySpan<byte> data)
    {
        return ExtractAsciiStrings(data)
            .Concat(ExtractUtf16LeStrings(data))
            .Distinct(StringComparer.Ordinal)
            .Where(static value => IsUsefulNearbyString(value))
            .Take(MaxNearbyStrings)
            .ToArray();
    }

    private static MetadataWindow BuildMetadataWindow(AssetMatch match)
    {
        byte[] context = ReadContext(match.FullPath, match.Offset, MetadataRadius, out long contextStart);
        List<MetadataString> strings = ExtractMetadataStrings(context, contextStart);

        return new MetadataWindow(
            match.RelativePath,
            match.FullPath,
            match.Layer,
            match.Offset,
            match.OffsetHex,
            match.MatchedString,
            contextStart,
            PickStrings(strings, static value => value.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)),
            PickStrings(strings, static value => value.EndsWith(".shadervariants", StringComparison.OrdinalIgnoreCase)),
            PickStrings(strings, static value => value.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)),
            PickStrings(strings, static value => IsTexturePath(value)),
            PickStrings(strings, static value => value.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)),
            PickStrings(strings, static value => value.Contains("svc_", StringComparison.OrdinalIgnoreCase)),
            PickStrings(strings, IsMaterialParameterCandidate),
            PickStrings(strings, static value => KeywordRegex.IsMatch(value)));
    }

    private static byte[] ReadContext(string path, long offset, int radius, out long contextStart)
    {
        FileInfo info = new(path);
        contextStart = Math.Max(0, offset - radius);
        int length = (int)Math.Min(radius * 2L, info.Length - contextStart);
        byte[] context = new byte[length];
        using FileStream stream = File.OpenRead(path);
        stream.Position = contextStart;
        int read = stream.Read(context, 0, context.Length);
        if (read < context.Length)
        {
            Array.Resize(ref context, read);
        }

        return context;
    }

    private static List<MetadataString> ExtractMetadataStrings(ReadOnlySpan<byte> data, long baseOffset)
    {
        List<MetadataString> result = new();
        AddAsciiStrings(data, baseOffset, result);
        AddUtf16LeStrings(data, baseOffset, result);
        return result
            .GroupBy(static item => (item.Offset, item.Value), new MetadataStringKeyComparer())
            .Select(static group => group.First())
            .OrderBy(static item => item.Offset)
            .ToList();
    }

    private static void AddAsciiStrings(ReadOnlySpan<byte> data, long baseOffset, List<MetadataString> result)
    {
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] is >= 0x20 and <= 0x7E)
            {
                if (start < 0)
                {
                    start = i;
                }
                continue;
            }

            AddAsciiMetadataString(data, baseOffset, result, start, i);
            start = -1;
        }

        AddAsciiMetadataString(data, baseOffset, result, start, data.Length);
    }

    private static void AddAsciiMetadataString(ReadOnlySpan<byte> data, long baseOffset, List<MetadataString> result, int start, int end)
    {
        if (start >= 0 && end - start >= 4)
        {
            result.Add(new MetadataString(baseOffset + start, Encoding.ASCII.GetString(data[start..end])));
        }
    }

    private static void AddUtf16LeStrings(ReadOnlySpan<byte> data, long baseOffset, List<MetadataString> result)
    {
        for (int parity = 0; parity < 2; parity++)
        {
            int start = -1;
            StringBuilder current = new();
            for (int i = parity; i + 1 < data.Length; i += 2)
            {
                ushort value = (ushort)(data[i] | (data[i + 1] << 8));
                if (value is >= 0x20 and <= 0x7E)
                {
                    if (start < 0)
                    {
                        start = i;
                    }
                    current.Append((char)value);
                    continue;
                }

                Flush();
            }

            Flush();

            void Flush()
            {
                if (start >= 0 && current.Length >= 4)
                {
                    result.Add(new MetadataString(baseOffset + start, current.ToString()));
                }
                start = -1;
                current.Clear();
            }
        }
    }

    private static IReadOnlyList<MetadataString> PickStrings(IEnumerable<MetadataString> strings, Func<string, bool> predicate)
    {
        return strings
            .Where(item => predicate(item.Value))
            .Where(static item => item.Value.Length <= 260)
            .DistinctBy(static item => item.Value, StringComparer.Ordinal)
            .Take(MaxMetadataItems)
            .ToArray();
    }

    private static bool IsTexturePath(string value)
    {
        return value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMaterialParameterCandidate(string value)
    {
        if (!PropertyNameRegex.IsMatch(value))
        {
            return false;
        }

        string[] usefulTokens =
        [
            "Color",
            "Map",
            "Tex",
            "Texture",
            "Blend",
            "Alpha",
            "Normal",
            "Metal",
            "Smooth",
            "Emission",
            "Base",
            "Main",
            "Mask",
            "Stencil",
            "Depth",
            "Atmosphere",
            "Environment",
            "Fog",
            "Cloud",
            "Water",
            "Wet",
            "Tile",
            "Flip",
            "Pass",
            "Irradiance",
            "Atlas",
            "Global",
            "Material",
        ];

        return usefulTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractMatchedString(ReadOnlySpan<byte> context, long relativeOffset, string encodingName)
    {
        if (relativeOffset < 0 || relativeOffset >= context.Length)
        {
            return null;
        }

        return encodingName.Equals("utf16le", StringComparison.OrdinalIgnoreCase)
            ? ExtractUtf16LeStringAt(context, (int)relativeOffset)
            : ExtractAsciiStringAt(context, (int)relativeOffset);
    }

    private static string? ExtractAsciiStringAt(ReadOnlySpan<byte> data, int offset)
    {
        int start = offset;
        while (start > 0 && data[start - 1] is >= 0x20 and <= 0x7E)
        {
            start--;
        }

        int end = offset;
        while (end < data.Length && data[end] is >= 0x20 and <= 0x7E)
        {
            end++;
        }

        return end - start >= 4 ? Encoding.ASCII.GetString(data[start..end]) : null;
    }

    private static string? ExtractUtf16LeStringAt(ReadOnlySpan<byte> data, int offset)
    {
        foreach (int parity in new[] { offset & 1, 1 - (offset & 1) })
        {
            int start = offset;
            if ((start & 1) != parity)
            {
                start--;
            }

            while (start - 2 >= 0)
            {
                ushort value = (ushort)(data[start - 2] | (data[start - 1] << 8));
                if (value == 0)
                {
                    break;
                }
                start -= 2;
            }

            int end = offset;
            if ((end & 1) != parity)
            {
                end--;
            }

            while (end + 1 < data.Length)
            {
                ushort value = (ushort)(data[end] | (data[end + 1] << 8));
                if (value == 0)
                {
                    break;
                }
                end += 2;
            }

            if (end <= start)
            {
                continue;
            }

            try
            {
                string value = Encoding.Unicode.GetString(data[start..end]);
                if (value.Length >= 4 && value.All(static c => c is >= ' ' and <= '~'))
                {
                    return value;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractAsciiStrings(ReadOnlySpan<byte> data)
    {
        List<string> strings = new();
        StringBuilder current = new();
        foreach (byte value in data)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                current.Append((char)value);
                continue;
            }

            Flush();
        }

        Flush();
        return strings;

        void Flush()
        {
            if (current.Length >= 4)
            {
                strings.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private static IReadOnlyList<string> ExtractUtf16LeStrings(ReadOnlySpan<byte> data)
    {
        List<string> strings = new();
        for (int parity = 0; parity < 2; parity++)
        {
            StringBuilder current = new();
            for (int i = parity; i + 1 < data.Length; i += 2)
            {
                ushort value = (ushort)(data[i] | (data[i + 1] << 8));
                if (value is >= 0x20 and <= 0x7E)
                {
                    current.Append((char)value);
                    continue;
                }

                Flush(current);
            }

            Flush(current);
        }

        return strings;

        void Flush(StringBuilder current)
        {
            if (current.Length >= 4)
            {
                strings.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private static bool IsUsefulNearbyString(string value)
    {
        return value.Contains("CAB-", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".ab", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".resS", StringComparison.OrdinalIgnoreCase)
            || value.Contains("HGRP", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Shader", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Material", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Texture", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Mesh", StringComparison.OrdinalIgnoreCase)
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal);
    }

    private sealed record SearchPattern(string QueryVariant, string EncodingName, byte[] Bytes);

    private sealed record AssetMatch(
        string RelativePath,
        string FullPath,
        string Layer,
        long FileLength,
        long Offset,
        string OffsetHex,
        string QueryVariant,
        string Encoding,
        string? MatchedString,
        long ContextStart,
        IReadOnlyList<string> NearbyStrings);

    private sealed record MetadataReport(
        string Query,
        string GameDataDirectory,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<MetadataQueryResult> QueryResults,
        string Note);

    private sealed record MetadataQueryResult(
        string Query,
        IReadOnlyList<AssetMatch> Matches,
        IReadOnlyList<MetadataWindow> Windows);

    private sealed record MetadataWindow(
        string RelativePath,
        string FullPath,
        string Layer,
        long Offset,
        string OffsetHex,
        string? MatchedString,
        long ContextStart,
        IReadOnlyList<MetadataString> ShaderPaths,
        IReadOnlyList<MetadataString> ShaderVariantPaths,
        IReadOnlyList<MetadataString> MaterialPaths,
        IReadOnlyList<MetadataString> TexturePaths,
        IReadOnlyList<MetadataString> AssetBundleNames,
        IReadOnlyList<MetadataString> ServiceNames,
        IReadOnlyList<MetadataString> MaterialParameterCandidates,
        IReadOnlyList<MetadataString> KeywordCandidates);

    private sealed record MetadataString(long Offset, string Value);

    private sealed class MetadataStringKeyComparer : IEqualityComparer<(long Offset, string Value)>
    {
        public bool Equals((long Offset, string Value) x, (long Offset, string Value) y)
        {
            return x.Offset == y.Offset && string.Equals(x.Value, y.Value, StringComparison.Ordinal);
        }

        public int GetHashCode((long Offset, string Value) obj)
        {
            return HashCode.Combine(obj.Offset, StringComparer.Ordinal.GetHashCode(obj.Value));
        }
    }

    private static class EndFieldJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false,
        };
    }
}
