namespace Ruri.RipperHook.Endfield;

public sealed record EndFieldVfsFile(string FileName, ulong FileNameHash, string FileChunkMd5Name, string FileDataMd5, ulong Offset, ulong Length, ulong IvSeed, uint Reserved, byte BlockType, bool UseEncrypt);
