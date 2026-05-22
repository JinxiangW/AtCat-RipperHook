namespace Ruri.RipperHook.EndField;

internal static class EndFieldIndexDecoder
{
    internal sealed record DecodedIndex(
        EndFieldIndexLoader.IndexLayer Layer,
        EndFieldIndexLoader.IndexKind Kind,
        string Path,
        bool Base64Decoded,
        int EncodedLength,
        int DecodedLength,
        byte[] RawBytes);

    public static List<DecodedIndex> DecodeAll(IEnumerable<EndFieldIndexLoader.IndexFile> files)
    {
        List<DecodedIndex> result = new();
        foreach (EndFieldIndexLoader.IndexFile file in files)
        {
            string normalized = file.RawText.Trim();
            try
            {
                byte[] bytes = Convert.FromBase64String(normalized);
                result.Add(new DecodedIndex(file.Layer, file.Kind, file.Path, true, normalized.Length, bytes.Length, bytes));
            }
            catch (FormatException)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                result.Add(new DecodedIndex(file.Layer, file.Kind, file.Path, false, normalized.Length, bytes.Length, bytes));
            }
        }
        return result;
    }
}
