namespace Ruri.RipperHook.EndField;

internal static class EndFieldVfsResolver
{
    public static IReadOnlyList<string> ResolveChunkInputs(EndFieldVfsManifest manifest)
    {
        return manifest.Files
            .Where(static file => file.IsChunk)
            .Select(static file => file.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
