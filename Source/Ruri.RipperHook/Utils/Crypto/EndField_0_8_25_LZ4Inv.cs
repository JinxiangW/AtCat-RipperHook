using System;

namespace Ruri.RipperHook.Crypto;

public class EndField_0_8_25_LZ4Inv : LZ4
{
	public new static EndField_0_8_25_LZ4Inv Instance { get; } = new EndField_0_8_25_LZ4Inv();

	protected override (int encCount, int litCount) GetLiteralToken(ReadOnlySpan<byte> cmp, ref int cmpPos)
	{
		byte b = cmp[cmpPos++];
		int num = b & 0x33;
		int num2 = b & 0xCC;
		num2 >>= 2;
		return (encCount: (num2 & 3) | (num2 >> 2), litCount: (num & 3) | (num >> 2));
	}

	protected override int GetChunkEnd(ReadOnlySpan<byte> cmp, ref int cmpPos)
	{
		return (cmp[cmpPos++] << 8) | cmp[cmpPos++];
	}
}
