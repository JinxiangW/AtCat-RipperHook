using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetRipper.IO.Endian;

namespace Ruri.RipperHook.Crypto;

public record VFSDecryptor : CommonDecryptor
{
	public VFSDecryptorVariant Variant { get; set; }

	private static readonly byte[] VFSAESSBox = new byte[256]
	{
		162, 183, 37, 71, 78, 130, 23, 208, 161, 98,
		134, 102, 189, 51, 160, 63, 145, 0, 64, 84,
		212, 125, 148, 176, 18, 88, 54, 131, 147, 154,
		82, 105, 8, 113, 7, 10, 57, 1, 215, 101,
		111, 39, 108, 216, 95, 252, 226, 85, 149, 164,
		194, 142, 91, 114, 14, 211, 198, 217, 210, 222,
		87, 206, 202, 96, 25, 19, 127, 132, 181, 90,
		86, 119, 244, 6, 229, 42, 55, 56, 157, 80,
		224, 92, 167, 218, 245, 153, 58, 13, 117, 74,
		15, 94, 230, 232, 150, 32, 207, 110, 27, 156,
		239, 233, 253, 106, 246, 116, 165, 72, 133, 89,
		20, 254, 247, 158, 115, 22, 140, 70, 138, 33,
		172, 38, 137, 191, 190, 203, 255, 5, 201, 243,
		81, 79, 192, 223, 11, 173, 66, 109, 146, 200,
		40, 112, 235, 12, 103, 118, 9, 199, 52, 48,
		65, 220, 69, 151, 159, 175, 236, 163, 129, 249,
		227, 75, 29, 177, 123, 251, 174, 126, 197, 36,
		234, 121, 135, 143, 53, 45, 97, 2, 219, 152,
		193, 248, 188, 214, 104, 169, 182, 73, 250, 50,
		225, 178, 228, 60, 136, 170, 21, 241, 30, 179,
		41, 4, 44, 168, 26, 67, 231, 205, 62, 187,
		34, 76, 107, 240, 141, 122, 68, 93, 61, 180,
		204, 124, 43, 49, 196, 144, 242, 28, 35, 100,
		184, 59, 213, 155, 16, 195, 237, 166, 83, 171,
		77, 120, 209, 186, 238, 24, 46, 47, 31, 221,
		128, 139, 185, 3, 17, 99
	};

	private static readonly byte[] VFSAESKey = new byte[16]
	{
		143, 194, 34, 208, 145, 203, 230, 143, 177, 246,
		97, 206, 145, 92, 255, 84
	};

	private static readonly byte[] VFSAESIV = new byte[16]
	{
		103, 35, 148, 239, 76, 213, 47, 118, 255, 222,
		123, 176, 106, 134, 98, 92
	};

	private static readonly byte[] CurrentVFSAESSBox = new byte[256]
	{
		228, 185, 69, 7, 146, 130, 47, 67, 245, 201,
		34, 37, 169, 79, 70, 109, 74, 113, 139, 108,
		140, 235, 178, 172, 207, 12, 158, 1, 56, 50,
		211, 147, 152, 99, 218, 150, 229, 196, 195, 107,
		127, 38, 114, 215, 151, 213, 128, 188, 93, 187,
		85, 103, 16, 115, 179, 141, 226, 53, 41, 71,
		168, 96, 63, 197, 239, 104, 236, 190, 171, 198,
		184, 92, 216, 21, 9, 84, 243, 122, 64, 162,
		48, 10, 220, 83, 250, 219, 241, 120, 222, 173,
		240, 181, 193, 129, 159, 62, 131, 144, 49, 242,
		251, 33, 40, 133, 6, 202, 205, 30, 212, 60,
		160, 200, 35, 22, 110, 137, 29, 231, 238, 94,
		66, 189, 203, 19, 80, 166, 78, 73, 88, 223,
		44, 132, 135, 182, 145, 82, 221, 25, 249, 43,
		77, 119, 186, 4, 165, 65, 206, 148, 61, 95,
		252, 155, 121, 154, 126, 101, 90, 177, 102, 52,
		86, 167, 26, 191, 234, 125, 39, 11, 89, 46,
		174, 20, 51, 192, 81, 57, 199, 58, 42, 157,
		244, 124, 204, 209, 214, 112, 55, 14, 117, 2,
		27, 227, 233, 72, 13, 36, 45, 247, 210, 183,
		175, 163, 161, 100, 123, 237, 248, 5, 149, 59,
		116, 253, 98, 208, 15, 255, 75, 170, 136, 91,
		3, 180, 232, 156, 176, 23, 28, 118, 87, 224,
		164, 68, 32, 217, 142, 17, 134, 105, 54, 254,
		76, 111, 97, 106, 143, 225, 24, 138, 18, 153,
		230, 31, 0, 8, 246, 194
	};

	private static readonly byte[] CurrentVFSAESKey = new byte[16]
	{
		58, 241, 140, 71, 178, 9, 109, 238, 81, 36,
		144, 124, 24, 211, 164, 98
	};

	private static readonly byte[] CurrentVFSAESIV = new byte[16]
	{
		199, 18, 94, 169, 4, 219, 51, 136, 242, 14,
		119, 73, 101, 186, 28, 147
	};

	private const ulong CurrentVFSXorKey = 17409429023445811606uL;

	private const ulong CB3VFSXorKey = 16042313282097494542uL;

	public VFSDecryptor(VFSDecryptorVariant variant = VFSDecryptorVariant.CB3)
	{
		Variant = variant;
	}

	public override Span<byte> Decrypt(Span<byte> buffer)
	{
		return Decrypt(buffer, Variant);
	}

	public Span<byte> Decrypt(Span<byte> buffer, VFSDecryptorVariant variant)
	{
		var (sBox, key, iv, xorKey) = GetKeys(variant);
		if (buffer.Length <= 256)
		{
			byte[] source = AESDecrypt(buffer.ToArray(), sBox, key, iv, xorKey);
			source.CopyTo(buffer);
		}
		else
		{
			int num = buffer.Length / 16;
			int num2 = 256 / num;
			if (num > 256)
			{
				num2 = 1;
			}
			Span<byte> span = new byte[256];
			for (int i = 0; i < Math.Min(num, 256); i++)
			{
				buffer.Slice(i * 16, num2).CopyTo(span.Slice(i * num2, num2));
			}
			byte[] array = AESDecrypt(span.ToArray(), sBox, key, iv, xorKey);
			for (int j = 0; j < Math.Min(num, 256); j++)
			{
				array.AsSpan(j * num2, num2).CopyTo(buffer.Slice(j * 16, num2));
			}
		}
		return buffer;
	}

	private static (byte[] SBox, byte[] Key, byte[] IV, ulong XorKey) GetKeys(VFSDecryptorVariant variant)
	{
		if (variant != VFSDecryptorVariant.Current)
		{
			return (SBox: VFSAESSBox, Key: VFSAESKey, IV: VFSAESIV, XorKey: 16042313282097494542uL);
		}
		return (SBox: CurrentVFSAESSBox, Key: CurrentVFSAESKey, IV: CurrentVFSAESIV, XorKey: 17409429023445811606uL);
	}

	public static bool IsValidHeader(EndianReader reader)
	{
		VFSDecryptorVariant variant;
		return IsValidHeader(reader, out variant);
	}

	public static bool IsValidHeader(EndianReader reader, out VFSDecryptorVariant variant)
	{
		variant = VFSDecryptorVariant.CB3;
		long position = reader.BaseStream.Position;
		EndianType endianType = reader.EndianType;
		try
		{
			reader.EndianType = EndianType.BigEndian;
			if (reader.BaseStream.Length - position < 8)
			{
				return false;
			}
			uint num = reader.ReadUInt32();
			uint num2 = reader.ReadUInt32();
			uint num3 = (4 * (num ^ 0x4A92F0CD)) & 0xFFFF0000u;
			uint num4 = BitOperations.RotateRight(num ^ 0x4A92F0CD, 14);
			uint num5 = num3 ^ num4 ^ 0xD8B1E637u;
			if (num2 == num5)
			{
				variant = VFSDecryptorVariant.Current;
				return true;
			}
			uint num6 = BitOperations.RotateRight(num ^ 0x91A64750u, 3);
			uint num7 = (num6 << 16) ^ 0xD5F9BECCu;
			uint num8 = (num6 ^ num7) & 0xFFFFFFFFu;
			if (num2 == num8)
			{
				variant = VFSDecryptorVariant.CB3;
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
		finally
		{
			reader.BaseStream.Position = position;
			reader.EndianType = endianType;
		}
	}

	public static uint BitConcat(int bits, params uint[] ns)
	{
		uint num = ((bits == 32) ? uint.MaxValue : ((uint)((1 << bits) - 1)));
		uint num2 = 0u;
		int num3 = ns.Length;
		for (int i = 0; i < num3; i++)
		{
			num2 |= (ns[i] & num) << bits * (num3 - i - 1);
		}
		return num2;
	}

	public static ulong BitConcat64(int bits, params ulong[] ns)
	{
		ulong num = (ulong)((bits == 64) ? (-1) : ((1L << bits) - 1));
		ulong num2 = 0uL;
		int num3 = ns.Length;
		for (int i = 0; i < num3; i++)
		{
			num2 |= (ns[i] & num) << bits * (num3 - i - 1);
		}
		return num2;
	}

	public static ushort RotateLeft16(ushort value, int count)
	{
		return (ushort)((value << count) | (value >> 16 - count));
	}

	public static ulong RotateLeft64(ulong value, int count)
	{
		return (value << count) | (value >> 64 - count);
	}

	public static ulong RotateRight64(ulong value, int count)
	{
		return (value >> count) | (value << 64 - count);
	}

	private static byte[] AESDecrypt(byte[] ciphertext, byte[] sBox, byte[] key, byte[] iv, ulong xorKey)
	{
		List<byte[]> list = new List<byte[]>();
		byte[] plaintext = iv.ToArray();
		foreach (byte[] item in SplitBlocks(ciphertext))
		{
			byte[] array = EncryptBlock(plaintext, sBox, key);
			byte[] array2 = new byte[item.Length];
			for (int i = 0; i < item.Length; i++)
			{
				array2[i] = (byte)(item[i] ^ array[i]);
			}
			list.Add(array2);
			byte[] array3 = new byte[16];
			int num = 0;
			for (int j = 0; j < 16; j++)
			{
				ulong num2 = xorKey >> (num & 0x38);
				byte b = (byte)(array[j] ^ (31 * j) ^ (byte)num2);
				num += 8;
				b = (byte)(((b >> 5) | (8 * b)) & 0xFF);
				b = sBox[b];
				array3[j] = b;
			}
			plaintext = array3;
		}
		return list.SelectMany((byte[] result) => result).ToArray();
	}

	private static byte[] EncryptBlock(byte[] plaintext, byte[] sBox, byte[] key)
	{
		List<byte[,]> list = ExpandKey(key, sBox);
		int num = 10;
		List<byte[]> list2 = BytesToMatrix(plaintext);
		AddRoundKey(list2, list[0]);
		for (int i = 1; i < num; i++)
		{
			SubBytes(list2, sBox);
			ShiftRows(list2);
			MixColumns(list2);
			AddRoundKey(list2, list[i]);
		}
		SubBytes(list2, sBox);
		ShiftRows(list2);
		AddRoundKey(list2, list[list.Count - 1]);
		return MatrixToBytes(list2);
	}

	private static IEnumerable<byte[]> SplitBlocks(byte[] msg, int blockSize = 16)
	{
		for (int i = 0; i < msg.Length; i += blockSize)
		{
			yield return msg.Skip(i).Take(blockSize).ToArray();
		}
	}

	private static void SubBytes(List<byte[]> s, byte[] sBox)
	{
		for (int i = 0; i < 4; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				s[i][j] = sBox[s[i][j]];
			}
		}
	}

	private static void ShiftRows(List<byte[]> s)
	{
		ref byte reference = ref s[0][1];
		ref byte reference2 = ref s[1][1];
		ref byte reference3 = ref s[2][1];
		ref byte reference4 = ref s[3][1];
		byte b = s[1][1];
		byte b2 = s[2][1];
		byte b3 = s[3][1];
		byte b4 = s[0][1];
		reference = b;
		reference2 = b2;
		reference3 = b3;
		reference4 = b4;
		reference3 = ref s[0][2];
		reference2 = ref s[1][2];
		reference = ref s[2][2];
		ref byte reference5 = ref s[3][2];
		b4 = s[2][2];
		b3 = s[3][2];
		b2 = s[0][2];
		b = s[1][2];
		reference3 = b4;
		reference2 = b3;
		reference = b2;
		reference5 = b;
		reference = ref s[0][3];
		reference2 = ref s[1][3];
		reference3 = ref s[2][3];
		ref byte reference6 = ref s[3][3];
		b = s[3][3];
		b2 = s[0][3];
		b3 = s[1][3];
		b4 = s[2][3];
		reference = b;
		reference2 = b2;
		reference3 = b3;
		reference6 = b4;
	}

	private static void AddRoundKey(List<byte[]> s, byte[,] k)
	{
		for (int i = 0; i < 4; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				s[i][j] ^= k[i, j];
			}
		}
	}

	private static byte XTime(byte a)
	{
		return (byte)(((a & 0x80) != 0) ? (((a << 1) ^ 0x1B) & 0xFF) : (a << 1));
	}

	private static void MixSingleColumn(byte[] a)
	{
		byte b = (byte)(a[0] ^ a[1] ^ a[2] ^ a[3]);
		byte b2 = a[0];
		a[0] ^= (byte)(b ^ XTime((byte)(a[0] ^ a[1])));
		a[1] ^= (byte)(b ^ XTime((byte)(a[1] ^ a[2])));
		a[2] ^= (byte)(b ^ XTime((byte)(a[2] ^ a[3])));
		a[3] ^= (byte)(b ^ XTime((byte)(a[3] ^ b2)));
	}

	private static void MixColumns(List<byte[]> s)
	{
		for (int i = 0; i < 4; i++)
		{
			MixSingleColumn(s[i]);
		}
	}

	private static List<byte[,]> ExpandKey(byte[] masterKey, byte[] sBox)
	{
		int num = 10;
		List<byte[]> list = BytesToMatrix(masterKey);
		int num2 = masterKey.Length / 4;
		int num3 = 1;
		byte[] array = new byte[32]
		{
			0, 1, 2, 4, 8, 16, 32, 64, 128, 27,
			54, 108, 216, 171, 77, 154, 47, 94, 188, 99,
			198, 151, 53, 106, 212, 179, 125, 250, 239, 197,
			145, 57
		};
		while (list.Count < (num + 1) * 4)
		{
			byte[] array2 = list[list.Count - 1].ToArray();
			if (list.Count % num2 == 0)
			{
				byte b = array2[0];
				Array.Copy(array2, 1, array2, 0, array2.Length - 1);
				array2[^1] = b;
				for (int i = 0; i < 4; i++)
				{
					array2[i] = sBox[array2[i]];
				}
				array2[0] ^= array[num3];
				num3++;
			}
			else if (masterKey.Length == 32 && list.Count % num2 == 4)
			{
				for (int j = 0; j < 4; j++)
				{
					array2[j] = sBox[array2[j]];
				}
			}
			byte[] array3 = list[list.Count - num2];
			for (int k = 0; k < 4; k++)
			{
				array2[k] ^= array3[k];
			}
			list.Add(array2);
		}
		List<byte[,]> list2 = new List<byte[,]>();
		for (int l = 0; l < list.Count / 4; l++)
		{
			byte[,] array4 = new byte[4, 4];
			for (int m = 0; m < 4; m++)
			{
				for (int n = 0; n < 4; n++)
				{
					array4[m, n] = list[l * 4 + m][n];
				}
			}
			list2.Add(array4);
		}
		return list2;
	}

	private static List<byte[]> BytesToMatrix(byte[] text)
	{
		List<byte[]> list = new List<byte[]>();
		for (int i = 0; i < text.Length; i += 4)
		{
			list.Add(new byte[4]
			{
				text[i],
				text[i + 1],
				text[i + 2],
				text[i + 3]
			});
		}
		return list;
	}

	private static byte[] MatrixToBytes(List<byte[]> m)
	{
		byte[] array = new byte[16];
		int num = 0;
		for (int i = 0; i < 4; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				array[num++] = m[i][j];
			}
		}
		return array;
	}
}
