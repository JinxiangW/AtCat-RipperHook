using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ruri.RipperHook.Endfield;

public static class EndField_0_8_25_Vfs
{
	private ref struct SpanReader(ReadOnlySpan<byte> data)
	{
		private readonly ReadOnlySpan<byte> _data = data;

		private int _position = 0;

		public void Skip(int count)
		{
			Ensure(count);
			_position += count;
		}

		public byte ReadByte()
		{
			Ensure(1);
			return _data[_position++];
		}

		public int ReadInt32()
		{
			Ensure(4);
			int result = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, 4));
			_position += 4;
			return result;
		}

		public uint ReadUInt32()
		{
			Ensure(4);
			uint result = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
			_position += 4;
			return result;
		}

		public long ReadInt64()
		{
			Ensure(8);
			long result = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position, 8));
			_position += 8;
			return result;
		}

		public ulong ReadUInt64()
		{
			Ensure(8);
			ulong result = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
			_position += 8;
			return result;
		}

		public string ReadString()
		{
			Ensure(2);
			int num = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
			_position += 2;
			Ensure(num);
			string result = Encoding.UTF8.GetString(_data.Slice(_position, num));
			_position += num;
			return result;
		}

		public string ReadUInt128Hex()
		{
			Ensure(16);
			ReadOnlySpan<byte> bytes = _data.Slice(_position, 16);
			_position += 16;
			return Convert.ToHexString(bytes);
		}

		private void Ensure(int count)
		{
			if (count < 0 || _position + count > _data.Length)
			{
				throw new InvalidDataException("unexpected end of VFS block info");
			}
		}
	}

	private const int VfsProtoVersion = 3;

	private const int BlockInfoHeaderSize = 12;

	private static readonly byte[] CommonChachaKey = CreateCommonChachaKey();

	public static bool IsVfsPath(string filePath)
	{
		if (!filePath.Contains($"{Path.DirectorySeparatorChar}VFS{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
		{
			return filePath.Contains($"{Path.AltDirectorySeparatorChar}VFS{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool IsBlockInfoPath(string filePath)
	{
		if (IsVfsPath(filePath))
		{
			return filePath.EndsWith(".blc", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool IsChunkPath(string filePath)
	{
		if (IsVfsPath(filePath))
		{
			return filePath.EndsWith(".chk", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool IsCurrentBlockInfoHeader(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 12)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4)) == 3;
		}
		return false;
	}

	public static bool TryParseBlockInfo(string blockInfoPath, out EndFieldVfsBlock block, out string error)
	{
		block = null;
		error = string.Empty;
		byte[] array;
		try
		{
			array = File.ReadAllBytes(blockInfoPath);
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		return TryParseBlockInfo(blockInfoPath, array, out block, out error);
	}

	public static bool TryParseBlockInfo(string blockInfoPath, ReadOnlySpan<byte> data, out EndFieldVfsBlock block, out string error)
	{
		block = null;
		error = string.Empty;
		try
		{
			if (!IsCurrentBlockInfoHeader(data))
			{
				error = "not a current Endfield VFS block info file";
				return false;
			}
			byte[] array = data.ToArray();
			Xxe1Transform(array.AsSpan(12), CommonChachaKey, array.AsSpan(0, 12), 1u);
			SpanReader spanReader = new SpanReader(array);
			spanReader.Skip(12);
			int num = spanReader.ReadInt32();
			int version = spanReader.ReadInt32();
			string groupName = spanReader.ReadString();
			long groupHashName = spanReader.ReadInt64();
			int groupFileCount = spanReader.ReadInt32();
			long groupChunksLength = spanReader.ReadInt64();
			byte b = spanReader.ReadByte();
			int num2 = spanReader.ReadInt32();
			List<EndFieldVfsChunk> list = new List<EndFieldVfsChunk>(num2);
			for (int i = 0; i < num2; i++)
			{
				string md5Name = spanReader.ReadUInt128Hex();
				string contentMd = spanReader.ReadUInt128Hex();
				long length = spanReader.ReadInt64();
				byte mainTag = spanReader.ReadByte();
				int blockType = ((num > 3) ? spanReader.ReadInt32() : b);
				int num3 = spanReader.ReadInt32();
				EndFieldVfsChunk endFieldVfsChunk = new EndFieldVfsChunk(md5Name, contentMd, length, mainTag, blockType);
				for (int j = 0; j < num3; j++)
				{
					string fileName = spanReader.ReadString();
					ulong fileNameHash = spanReader.ReadUInt64();
					string fileChunkMd5Name = spanReader.ReadUInt128Hex();
					string fileDataMd = spanReader.ReadUInt128Hex();
					ulong offset = spanReader.ReadUInt64();
					ulong length2 = spanReader.ReadUInt64();
					byte blockType2 = spanReader.ReadByte();
					bool flag = spanReader.ReadByte() != 0;
					ulong ivSeed = (flag ? spanReader.ReadUInt64() : 0);
					uint reserved = ((!flag && num > 3) ? spanReader.ReadUInt32() : 0u);
					endFieldVfsChunk.Files.Add(new EndFieldVfsFile(fileName, fileNameHash, fileChunkMd5Name, fileDataMd, offset, length2, ivSeed, reserved, blockType2, flag));
				}
				list.Add(endFieldVfsChunk);
			}
			block = new EndFieldVfsBlock(blockInfoPath, num, version, groupName, groupHashName, groupFileCount, groupChunksLength, b, list);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	public static bool TryReadFile(EndFieldVfsBlock block, EndFieldVfsChunk chunk, EndFieldVfsFile file, out byte[] data, out string error)
	{
		data = null;
		error = string.Empty;
		if (file.Length > int.MaxValue)
		{
			error = $"file is too large to buffer: {file.Length}";
			return false;
		}
		string directoryName = Path.GetDirectoryName(block.BlockInfoPath);
		if (string.IsNullOrEmpty(directoryName))
		{
			error = "missing VFS block directory";
			return false;
		}
		string text = Path.Combine(directoryName, chunk.Md5Name + ".chk");
		if (!File.Exists(text))
		{
			error = "missing chunk file: " + text;
			return false;
		}
		try
		{
			using FileStream fileStream = File.OpenRead(text);
			ulong num = file.Offset + file.Length;
			if (num > (ulong)fileStream.Length || num < file.Offset)
			{
				error = $"slice is outside chunk bounds: {file.Offset}+{file.Length} > {fileStream.Length}";
				return false;
			}
			data = new byte[(uint)file.Length];
			fileStream.Position = (long)file.Offset;
			fileStream.ReadExactly(data);
			if (file.UseEncrypt)
			{
				Span<byte> span = stackalloc byte[12];
				BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), 3);
				BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(4, 8), file.IvSeed);
				Xxe1Transform(data, CommonChachaKey, span, 1u);
			}
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static byte[] CreateCommonChachaKey()
	{
		string[] array = new string[8] { "Ks6k3zhrV5g", "uOVtMpqHxFv", "gi4BZzU9xUY", "CnBfZVqAgL", "SjpNhdKK89V", "qzl3BC/08Da", "oXafvEwR54", "4ZzYokf5I7Z" };
		byte[] array2 = Convert.FromBase64String(array[0] + array[3] + array[5] + array[2] + "=");
		byte[] bytes = Encoding.ASCII.GetBytes("Assets/Beyond/DynamicAssets/");
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i] -= bytes[i % bytes.Length];
		}
		return array2;
	}

	private static void Xxe1Transform(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
	{
		Span<uint> span = stackalloc uint[16];
		span[0] = 1634760805u;
		span[1] = 857760878u;
		span[2] = 2036477234u;
		span[3] = 1797285236u;
		for (int i = 0; i < 8; i++)
		{
			span[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
		}
		span[12] = counter;
		span[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
		span[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
		span[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));
		Span<byte> destination = stackalloc byte[64];
		int num;
		for (int j = 0; j < data.Length; j += num)
		{
			WriteXxe1Block(span, destination);
			span[12]++;
			num = Math.Min(destination.Length, data.Length - j);
			for (int k = 0; k < num; k++)
			{
				data[j + k] ^= destination[k];
			}
		}
	}

	private static void WriteXxe1Block(ReadOnlySpan<uint> state, Span<byte> destination)
	{
		Span<uint> span = stackalloc uint[16];
		state.CopyTo(span);
		for (int i = 0; i < 10; i++)
		{
			QuarterRound(span, 0, 4, 8, 12);
			QuarterRound(span, 1, 5, 9, 13);
			QuarterRound(span, 2, 6, 10, 14);
			QuarterRound(span, 3, 7, 11, 15);
			QuarterRound(span, 0, 5, 10, 15);
			QuarterRound(span, 1, 6, 11, 12);
			QuarterRound(span, 2, 7, 8, 13);
			QuarterRound(span, 3, 4, 9, 14);
		}
		for (int j = 0; j < span.Length; j++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(j * 4, 4), span[j] + state[j]);
		}
	}

	private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
	{
		state[a] += state[b];
		state[d] = uint.RotateLeft(state[d] ^ state[a], 16);
		state[c] += state[d];
		state[b] = uint.RotateLeft(state[b] ^ state[c], 12);
		state[a] += state[b];
		state[d] = uint.RotateLeft(state[d] ^ state[a], 8);
		state[c] += state[d];
		state[b] = uint.RotateLeft(state[b] ^ state[c], 7);
	}
}
