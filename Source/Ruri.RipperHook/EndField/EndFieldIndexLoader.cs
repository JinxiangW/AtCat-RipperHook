using System.Text;

namespace Ruri.RipperHook.EndField;

internal static class EndFieldIndexLoader
{
    internal enum IndexLayer
    {
        StreamingAssets = 0,
        Persistent = 1,
    }

    internal enum IndexKind
    {
        Initial = 0,
        Main = 1,
    }

    internal sealed record IndexFile(IndexLayer Layer, IndexKind Kind, string Path, string RawText);

    public static List<IndexFile> LoadAll(string gameDataDirectory)
    {
        List<IndexFile> result = new();
        TryAdd(result, gameDataDirectory, IndexLayer.StreamingAssets, IndexKind.Initial, Path.Combine(gameDataDirectory, "StreamingAssets", "index_initial.json"));
        TryAdd(result, gameDataDirectory, IndexLayer.StreamingAssets, IndexKind.Main, Path.Combine(gameDataDirectory, "StreamingAssets", "index_main.json"));
        TryAdd(result, gameDataDirectory, IndexLayer.Persistent, IndexKind.Initial, Path.Combine(gameDataDirectory, "Persistent", "index_initial.json"));
        TryAdd(result, gameDataDirectory, IndexLayer.Persistent, IndexKind.Main, Path.Combine(gameDataDirectory, "Persistent", "index_main.json"));
        return result;
    }

    private static void TryAdd(List<IndexFile> result, string gameDataDirectory, IndexLayer layer, IndexKind kind, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string raw = File.ReadAllText(path, Encoding.UTF8);
        result.Add(new IndexFile(layer, kind, path, raw));
    }
}
