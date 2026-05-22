using System.Collections.Generic;

namespace Ruri.RipperHook.Endfield;

public sealed record EndFieldVfsBlock(string BlockInfoPath, int CodeVersion, int Version, string GroupName, long GroupHashName, int GroupFileCount, long GroupChunksLength, byte BlockType, IReadOnlyList<EndFieldVfsChunk> Chunks);
