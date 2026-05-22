using AssetRipper.Import.Logging;
using System.Text;

namespace Ruri.RipperHook.EndField;

internal static class EndFieldNativeConsumerProbe
{
    private static readonly string[] Needles =
    [
        "StreamingSceneV2",
        "StreamingSceneNative_Create",
        "StreamingScene",
        "FlatBuffer",
        "VFS",
        "index_main",
        "archive:/CAB-",
        ".chk",
        ".blc",
    ];

    public static void LogSummary(string gameDataDirectory)
    {
        string? gameRoot = Directory.GetParent(gameDataDirectory)?.FullName;
        string? gameAssembly = gameRoot is null ? null : Path.Combine(gameRoot, "GameAssembly.dll");
        string metadata = Path.Combine(gameDataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat");

        Logger.Info(LogCategory.Import,
            $"[EndField] native probe: gameAssembly={File.Exists(gameAssembly)} metadata={File.Exists(metadata)}");

        if (gameAssembly is null || !File.Exists(gameAssembly))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(gameAssembly);
        foreach (string needle in Needles)
        {
            int ascii = IndexOf(bytes, Encoding.ASCII.GetBytes(needle));
            int utf16 = IndexOf(bytes, Encoding.Unicode.GetBytes(needle));
            if (ascii >= 0 || utf16 >= 0)
            {
                Logger.Info(LogCategory.Import,
                    $"[EndField] native string: {needle} ascii={ascii} utf16={utf16}");
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
}
