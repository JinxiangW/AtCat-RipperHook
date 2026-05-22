using System.Collections.Generic;

namespace Ruri.RipperHook.Endfield;

public sealed record EndFieldVfsChunk(string Md5Name, string ContentMd5, long Length, byte MainTag, int BlockType)
{
	public List<EndFieldVfsFile> Files { get; } = new List<EndFieldVfsFile>();
}
