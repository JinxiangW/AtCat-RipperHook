using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.IO.Files.Extensions;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.VertexData;
using K4os.Compression.LZ4;
using Ruri.Hook.Attributes;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;

namespace Ruri.RipperHook.Endfield;

[TypeTreeHook(ClassIDType.AnimationClip)]
[TypeTreeHook(ClassIDType.Animator)]
[TypeTreeHook(ClassIDType.AnimatorController)]
[TypeTreeHook(ClassIDType.AnimatorOverrideController)]
[TypeTreeHook(ClassIDType.AssetBundle)]
[TypeTreeHook(ClassIDType.AudioManager)]
[TypeTreeHook(ClassIDType.BillboardRenderer)]
[TypeTreeHook(ClassIDType.BoxCollider)]
[TypeTreeHook(ClassIDType.CanvasGroup)]
[TypeTreeHook(ClassIDType.CapsuleCollider)]
[TypeTreeHook(ClassIDType.CharacterController)]
[TypeTreeHook(ClassIDType.Cubemap)]
[TypeTreeHook(ClassIDType.CubemapArray)]
[TypeTreeHook(ClassIDType.CustomRenderTexture)]
[TypeTreeHook(ClassIDType.GraphicsSettings)]
[TypeTreeHook(ClassIDType.Light)]
[TypeTreeHook(ClassIDType.LineRenderer)]
[TypeTreeHook(ClassIDType.Material)]
[TypeTreeHook(ClassIDType.MeshCollider)]
[TypeTreeHook(ClassIDType.MeshRenderer)]
[TypeTreeHook(ClassIDType.NavMeshData_238)]
[TypeTreeHook(ClassIDType.NavMeshProjectSettings)]
[TypeTreeHook(ClassIDType.ParticleSystem)]
[TypeTreeHook(ClassIDType.ParticleSystemRenderer)]
[TypeTreeHook(ClassIDType.ProceduralMaterial)]
[TypeTreeHook(ClassIDType.QualitySettings)]
[TypeTreeHook(ClassIDType.ReflectionProbe)]
[TypeTreeHook(ClassIDType.RenderSettings)]
[TypeTreeHook(ClassIDType.RenderTexture)]
[TypeTreeHook(ClassIDType.Shader)]
[TypeTreeHook(ClassIDType.SkinnedMeshRenderer)]
[TypeTreeHook(ClassIDType.SparseTexture)]
[TypeTreeHook(ClassIDType.SphereCollider)]
[TypeTreeHook(ClassIDType.SpriteMask)]
[TypeTreeHook(ClassIDType.SpriteRenderer)]
[TypeTreeHook(ClassIDType.SpriteShapeRenderer)]
[TypeTreeHook(ClassIDType.TagManager)]
[TypeTreeHook(ClassIDType.Terrain)]
[TypeTreeHook(ClassIDType.TerrainCollider)]
[TypeTreeHook(ClassIDType.TerrainData)]
[TypeTreeHook(ClassIDType.TerrainLayer)]
[TypeTreeHook(ClassIDType.Texture2D)]
[TypeTreeHook(ClassIDType.Texture2DArray)]
[TypeTreeHook(ClassIDType.Texture3D)]
[TypeTreeHook(ClassIDType.TilemapRenderer)]
[TypeTreeHook(ClassIDType.TrailRenderer)]
[TypeTreeHook(ClassIDType.VFXRenderer)]
[RipperHook(GameType.EndField, "0.8.25", "2021.3.34f1")]
public class EndField_0_8_25_Hook : EndFieldCommon_Hook
{
	public static string customVersion = $"2021.3.825x{5}";

	public static UnityVersion endFieldClassVersion = UnityVersion.Parse(customVersion);

	private static VFSDecryptor vfsDecryptor;

	private static VFSDecryptorVariant vfsBundleVariant = VFSDecryptorVariant.Current;

	[RetargetMethod(typeof(Mesh_2020_1_0_a19))]
	public void Mesh_ReadRelease(ref EndianSpanReader reader)
	{
		Mesh_2020_1_0_a19 mesh = (object)this as Mesh_2020_1_0_a19;
		Type meshType = typeof(Mesh_2020_1_0_a19);
		mesh.Name = reader.ReadRelease_Utf8StringAlign();
		SetAssetListField<SubMesh_2017_3>(meshType, "m_SubMeshes", ref reader);
		mesh.Shapes.ReadRelease(ref reader);
		mesh.BindPose.ReadRelease_ArrayAlign_Asset(ref reader);
		mesh.BoneNameHashes.ReadRelease_ArrayAlign_UInt32(ref reader);
		mesh.RootBoneNameHash = reader.ReadUInt32();
		mesh.BonesAABB.ReadRelease_ArrayAlign_Asset(ref reader);
		mesh.VariableBoneCountWeights.ReadRelease(ref reader);
		mesh.MeshCompression = reader.ReadByte();
		if (mesh.MeshCompression == 4)
		{
			mesh.MeshCompression = 0;
		}
		mesh.IsReadable = reader.ReadBoolean();
		mesh.KeepVertices = reader.ReadBoolean();
		mesh.KeepIndices = reader.ReadBoolean();
		reader.ReadBoolean();
		bool hasCompressedMesh = reader.ReadBoolean();
		reader.ReadRelease_BooleanAlign();
		mesh.IndexFormat = reader.ReadInt32();
		mesh.IndexBuffer = reader.ReadRelease_ArrayAlign_Byte();
		((VertexData_2019)mesh.VertexData).ReadRelease_AssetAlign(ref reader);
		if (!hasCompressedMesh)
		{
			mesh.CompressedMesh.ReadRelease(ref reader);
		}
		mesh.LocalAABB.ReadRelease(ref reader);
		mesh.MeshUsageFlags = reader.ReadInt32();
		mesh.BakedConvexCollisionMesh = reader.ReadRelease_ArrayAlign_Byte();
		mesh.BakedTriangleCollisionMesh = reader.ReadRelease_ArrayAlign_Byte();
		mesh.MeshMetrics_0_ = reader.ReadSingle();
		mesh.MeshMetrics_1_ = reader.ReadSingle();
		reader.ReadRelease_SingleAlign();
		mesh.StreamData.ReadRelease(ref reader);
		if (reader.Length - reader.Position >= 4)
		{
			reader.ReadUInt32();
		}
	}

	public static void CustomBlockCompression(FileStreamNode entry, Stream m_stream, StorageBlock block, SmartStream m_cachedBlockStream, CompressionType compressType, int m_cachedBlockIndex)
	{
		switch (compressType)
		{
		case CompressionType.Lzma:
			LzmaCompression.DecompressLzmaStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
			break;
		case CompressionType.Lz4:
		case CompressionType.Lz4HC:
		{
			uint uncompressedSize = block.UncompressedSize;
			byte[] array = new byte[uncompressedSize];
			Span<byte> span = new BinaryReader(m_stream).ReadBytes((int)block.CompressedSize);
			int num = LZ4Codec.Decode(span, array);
			if (num != uncompressedSize)
			{
				ARIntelnalReflection.ThrowIncorrectNumberBytesWrittenMethod.Invoke(null, new object[4]
				{
					entry.PathFixed,
					compressType,
					(long)uncompressedSize,
					(long)num
				});
			}
			new MemoryStream(array).CopyTo(m_cachedBlockStream);
			break;
		}
		case (CompressionType)5:
		{
			int compressedSize = (int)block.CompressedSize;
			int uncompressedSize2 = (int)block.UncompressedSize;
			byte[] array2 = new BinaryReader(m_stream).ReadBytes(compressedSize);
			byte[] array3 = new byte[uncompressedSize2];
			vfsDecryptor.Decrypt(array2, vfsBundleVariant);
			int num2 = EndField_0_8_25_LZ4Inv.Instance.Decompress(array2, array3);
			if (num2 != uncompressedSize2)
			{
				Logger.Error($"[EndField] Block {m_cachedBlockIndex} decompression CRITICAL failure. Expected {uncompressedSize2}, Got {num2}.");
			}
			new MemoryStream(array3).CopyTo(m_cachedBlockStream);
			break;
		}
		default:
			if (ZstdCompression.IsZstd(m_stream))
			{
				ZstdCompression.DecompressStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
			}
			else
			{
				UnsupportedBundleDecompression.Throw("UnsupportedBundleDecompression", compressType);
			}
			break;
		}
	}

	[RetargetMethod(typeof(FileStreamBundleFile), "ReadFileStreamMetadata", new Type[] { })]
	public void ReadFileStreamMetadata(Stream stream, long basePosition)
	{
		FileStreamBundleFile fileStreamBundleFile = (object)this as FileStreamBundleFile;
		FileStreamBundleHeader header = fileStreamBundleFile.Header;
		if (header.Version >= BundleVersion.BF_LargeFilesSupport)
		{
			stream.Align(16);
		}
		if (header.Flags.GetBlocksInfoAtTheEnd())
		{
			stream.Position = basePosition + (header.Size - header.CompressedBlocksInfoSize);
		}
		int compressedBlocksInfoSize = header.CompressedBlocksInfoSize;
		int uncompressedBlocksInfoSize = header.UncompressedBlocksInfoSize;
		byte[] array = new BinaryReader(stream).ReadBytes(compressedBlocksInfoSize);
		MemoryStream stream2;
		if ((header.Flags & BundleFlags.CompressionTypeMask) != BundleFlags.None)
		{
			vfsDecryptor.Decrypt(array, vfsBundleVariant);
			byte[] array2 = new byte[uncompressedBlocksInfoSize];
			int num = LZ4Codec.Decode(array, array2);
			if (num != uncompressedBlocksInfoSize)
			{
				Logger.Warning($"[EndField] Metadata decompression mismatch. Expected {uncompressedBlocksInfoSize}, got {num}");
			}
			stream2 = new MemoryStream(array2);
		}
		else
		{
			stream2 = new MemoryStream(array);
		}
		using (EndianReader reader = new EndianReader(stream2, EndianType.BigEndian))
		{
			BlocksInfo value = ReadObfuscatedBlocksInfo(reader);
			SetPrivateProperty(fileStreamBundleFile, "BlocksInfo", value);
			DirectoryInfo<FileStreamNode> directoryInfo = ReadObfuscatedDirectoryInfo(reader);
			fileStreamBundleFile.DirectoryInfo = directoryInfo;
		}
		if ((header.Flags & BundleFlags.BlockInfoNeedPaddingAtStart) != BundleFlags.None)
		{
			stream.Align(16);
		}
	}

	private static BlocksInfo ReadObfuscatedBlocksInfo(EndianReader reader)
	{
		EndianType endianType = reader.EndianType;
		reader.EndianType = EndianType.BigEndian;
		uint num;
		if (vfsBundleVariant == VFSDecryptorVariant.Current)
		{
			reader.EndianType = EndianType.LittleEndian;
			num = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x8A7BF723u);
			reader.EndianType = EndianType.BigEndian;
		}
		else
		{
			num = reader.ReadUInt32() ^ 0xF6825038u;
		}
		uint num2 = num & 0xFFFF;
		uint num3 = (num >> 16) & 0xFFFF;
		uint value = VFSDecryptor.BitConcat(16, num2 ^ num3, num2);
		value = ((vfsBundleVariant == VFSDecryptorVariant.Current) ? (BitOperations.RotateRight(value, 18) ^ 0x91CE0A4Fu) : (BitOperations.RotateLeft(value, 3) ^ 0x5F23A227));
		StorageBlock[] array = new StorageBlock[value];
		for (int i = 0; i < value; i++)
		{
			ushort num4;
			ushort num5;
			ushort num6;
			ushort num7;
			ushort num8;
			if (vfsBundleVariant == VFSDecryptorVariant.Current)
			{
				num4 = reader.ReadUInt16();
				num5 = reader.ReadUInt16();
				num6 = reader.ReadUInt16();
				num7 = (ushort)(reader.ReadUInt16() ^ 0x9CD6);
				num8 = reader.ReadUInt16();
			}
			else
			{
				num7 = (ushort)(reader.ReadUInt16() ^ 0xAFEB);
				num4 = reader.ReadUInt16();
				num5 = reader.ReadUInt16();
				num6 = reader.ReadUInt16();
				num8 = reader.ReadUInt16();
			}
			uint num9 = (ushort)(num7 & 0xFF);
			uint num10 = (ushort)((num7 >> 8) & 0xFF);
			ushort value2 = (ushort)VFSDecryptor.BitConcat(8, num9 ^ num10, num9);
			uint value3;
			uint value4;
			if (vfsBundleVariant == VFSDecryptorVariant.Current)
			{
				value2 = (ushort)(num6 ^ VFSDecryptor.RotateLeft16(value2, 14) ^ 0x523F);
				value3 = VFSDecryptor.BitConcat(16, (uint)(num4 ^ num6 ^ 0xA121), num6);
				value3 = BitOperations.RotateRight(value3, 18) ^ 0xF74324EEu;
				value4 = VFSDecryptor.BitConcat(16, (uint)(num5 ^ num8 ^ 0xA121), num8);
				value4 = BitOperations.RotateRight(value4, 18) ^ 0xF74324EEu;
			}
			else
			{
				value2 = (ushort)(num5 ^ VFSDecryptor.RotateLeft16(value2, 3) ^ 0xB7AF);
				value3 = VFSDecryptor.BitConcat(16, (uint)(num6 ^ num5 ^ 0xE114), num5);
				value3 = BitOperations.RotateLeft(value3, 3) ^ 0x5ADA4ABA;
				value4 = VFSDecryptor.BitConcat(16, (uint)(num8 ^ num4 ^ 0xE114), num4);
				value4 = BitOperations.RotateLeft(value4, 3) ^ 0x5ADA4ABA;
			}
			StorageBlock storageBlock = new StorageBlock();
			SetPrivateProperty(storageBlock, "CompressedSize", value4);
			SetPrivateProperty(storageBlock, "UncompressedSize", value3);
			SetPrivateProperty(storageBlock, "Flags", (StorageBlockFlags)value2);
			array[i] = storageBlock;
		}
		reader.EndianType = endianType;
		return new BlocksInfo(new Hash128(), array);
	}

	private static DirectoryInfo<FileStreamNode> ReadObfuscatedDirectoryInfo(EndianReader reader)
	{
		EndianType endianType = reader.EndianType;
		reader.EndianType = EndianType.BigEndian;
		uint num;
		if (vfsBundleVariant == VFSDecryptorVariant.Current)
		{
			reader.EndianType = EndianType.LittleEndian;
			num = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x5DE50A6B);
			reader.EndianType = EndianType.BigEndian;
		}
		else
		{
			num = reader.ReadUInt32() ^ 0xA9535111u;
		}
		uint num2 = num & 0xFFFF;
		uint num3 = (num >> 16) & 0xFFFF;
		uint value = VFSDecryptor.BitConcat(16, num2 ^ num3, num2);
		value = ((vfsBundleVariant == VFSDecryptorVariant.Current) ? (BitOperations.RotateRight(value, 18) ^ 0xE4C1D9F2u) : (BitOperations.RotateLeft(value, 3) ^ 0xAF807AFCu));
		FileStreamNode[] array = new FileStreamNode[value];
		for (int i = 0; i < value; i++)
		{
			uint num4 = 0u;
			uint num5;
			uint num6;
			uint num7;
			if (vfsBundleVariant == VFSDecryptorVariant.Current)
			{
				num5 = reader.ReadUInt32() ^ 0x8E06A9F8u;
				num6 = reader.ReadUInt32();
				num7 = reader.ReadUInt32();
				num4 = reader.ReadUInt32();
			}
			else
			{
				num5 = reader.ReadUInt32();
				num6 = reader.ReadUInt32();
				num7 = reader.ReadUInt32();
			}
			List<byte> list = new List<byte>();
			int num8 = ((vfsBundleVariant == VFSDecryptorVariant.Current) ? 64 : 256);
			for (int j = 0; j < num8; j++)
			{
				byte b = reader.ReadByte();
				if (b == 0)
				{
					break;
				}
				list.Add(b);
			}
			string path;
			uint value2;
			ulong value3;
			ulong value4;
			if (vfsBundleVariant == VFSDecryptorVariant.Current)
			{
				for (int k = 0; k < list.Count; k++)
				{
					list[k] ^= (byte)((k ^ 0x97) & 0xFF);
				}
				path = Encoding.ASCII.GetString(list.ToArray());
				uint num9 = reader.ReadUInt32();
				uint num10 = num5 & 0xFFFF;
				uint num11 = (num5 >> 16) & 0xFFFF;
				value2 = VFSDecryptor.BitConcat(16, num11 ^ num10, num10);
				value2 = BitOperations.RotateRight(value2, 18) ^ 0xF13927C4u ^ num6;
				value3 = VFSDecryptor.BitConcat64(32, num4 ^ num7 ^ 0xDAD76848u, num7);
				value3 = VFSDecryptor.RotateLeft64(value3, 14) ^ 0xA4F1A11747816520uL;
				value4 = VFSDecryptor.BitConcat64(32, num6 ^ num9 ^ 0xDAD76848u, num9);
				value4 = VFSDecryptor.RotateLeft64(value4, 14) ^ 0xA4F1A11747816520uL;
			}
			else
			{
				path = new string(list.Select((byte b2) => (char)(b2 ^ 0xAC)).ToArray());
				num4 = reader.ReadUInt32() ^ 0xE4A15748u;
				uint num12 = reader.ReadUInt32();
				uint num13 = num4 & 0xFFFF;
				uint num14 = (num4 >> 16) & 0xFFFF;
				value2 = VFSDecryptor.BitConcat(16, num14 ^ num13, num13);
				value2 = BitOperations.RotateLeft(value2, 3) ^ 0x54D7A374 ^ num6;
				value3 = VFSDecryptor.BitConcat64(32, num7 ^ num5 ^ 0x342D983F, num5);
				value3 = VFSDecryptor.RotateLeft64(value3, 3) ^ 0x5B4FA98A430D0E62L;
				value4 = VFSDecryptor.BitConcat64(32, num12 ^ num6 ^ 0x342D983F, num6);
				value4 = VFSDecryptor.RotateLeft64(value4, 3) ^ 0x5B4FA98A430D0E62L;
			}
			FileStreamNode fileStreamNode = new FileStreamNode
			{
				Offset = (long)value3,
				Size = (long)value4,
				Flags = (NodeFlags)value2,
				Path = path
			};
			array[i] = fileStreamNode;
		}
		reader.EndianType = endianType;
		return new DirectoryInfo<FileStreamNode>
		{
			Nodes = array
		};
	}

	[RetargetMethod(typeof(FileStreamBundleHeader), "Read", new Type[] { })]
	public void Read(EndianReader reader)
	{
		FileStreamBundleHeader fileStreamBundleHeader = (object)this as FileStreamBundleHeader;
		fileStreamBundleHeader.Version = BundleVersion.BF_LargeFilesSupport;
		fileStreamBundleHeader.UnityWebBundleVersion = "5.x.x";
		fileStreamBundleHeader.UnityWebMinimumRevision = "2021.3.34f5";
		EndianType endianType = reader.EndianType;
		reader.EndianType = EndianType.BigEndian;
		long position = reader.BaseStream.Position;
		if (VFSDecryptor.IsValidHeader(reader, out var variant))
		{
			vfsBundleVariant = variant;
			vfsDecryptor.Variant = variant;
		}
		reader.ReadUInt32();
		reader.ReadUInt32();
		uint num;
		uint num2;
		uint num3;
		ulong num4;
		uint num5;
		uint num6;
		uint num7;
		ulong num8;
		uint num9;
		if (vfsBundleVariant == VFSDecryptorVariant.Current)
		{
			num = reader.ReadUInt16();
			num2 = reader.ReadUInt32();
			num3 = reader.ReadUInt32();
			num4 = reader.ReadUInt32();
			num5 = reader.ReadUInt32();
			num6 = reader.ReadUInt16();
			reader.ReadUInt32();
			num7 = reader.ReadUInt16();
			num8 = reader.ReadUInt32();
			num9 = reader.ReadUInt16();
			reader.ReadByte();
		}
		else
		{
			num5 = reader.ReadUInt32();
			num6 = reader.ReadUInt16();
			num = reader.ReadUInt16();
			reader.ReadUInt32();
			num7 = reader.ReadUInt16();
			num3 = reader.ReadUInt32();
			num8 = reader.ReadUInt32();
			num9 = reader.ReadUInt16();
			num2 = reader.ReadUInt32();
			num4 = reader.ReadUInt32();
		}
		uint num10 = num3 ^ num2;
		uint flags;
		uint value;
		uint value2;
		ulong value3;
		if (vfsBundleVariant == VFSDecryptorVariant.Current)
		{
			value = VFSDecryptor.BitConcat(16, num9 ^ num ^ 0xA121, num);
			value = BitOperations.RotateRight(value, 18) ^ 0xF74324EEu;
			value2 = VFSDecryptor.BitConcat(16, num6 ^ num7 ^ 0xA121, num7);
			value2 = BitOperations.RotateRight(value2, 18) ^ 0xF74324EEu;
			value3 = VFSDecryptor.BitConcat64(32, num8 ^ num4 ^ 0xDAD76848u, num4);
			value3 = VFSDecryptor.RotateRight64(value3, 18) ^ 0xA4F1A11747816520uL;
			flags = num5 ^ num2 ^ 0xA7F49310u;
		}
		else
		{
			value = VFSDecryptor.BitConcat(16, num ^ num9 ^ 0xE114, num9);
			value = BitOperations.RotateLeft(value, 3) ^ 0x5ADA4ABA;
			value2 = VFSDecryptor.BitConcat(16, num6 ^ num7 ^ 0xE114, num7);
			value2 = BitOperations.RotateLeft(value2, 3) ^ 0x5ADA4ABA;
			value3 = VFSDecryptor.BitConcat64(32, num8 ^ num4 ^ 0x342D983F, num4);
			value3 = BitOperations.RotateLeft(value3, 3) ^ 0x5B4FA98A430D0E62L;
			flags = num5 ^ num2 ^ 0x98B806A4u;
		}
		fileStreamBundleHeader.CompressedBlocksInfoSize = (int)value;
		fileStreamBundleHeader.UncompressedBlocksInfoSize = (int)value2;
		fileStreamBundleHeader.Size = (long)value3;
		fileStreamBundleHeader.Flags = (BundleFlags)flags;
		reader.BaseStream.Position = position + ((num10 >= 7) ? 48 : 40);
		reader.EndianType = endianType;
	}

	public static void CustomFilePreInitialize(GameBundle _this, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
	{
		foreach (string path in paths)
		{
			if (EndField_0_8_25_Vfs.IsBlockInfoPath(path))
			{
				LoadVfsBlockInfo(path, fileStack, dependencyProvider);
				continue;
			}
			if (EndField_0_8_25_Vfs.IsChunkPath(path))
			{
				Logger.Info("[EndField] Skipping raw VFS chunk: " + Path.GetFileName(path));
				continue;
			}
			using SmartStream smartStream = SmartStream.OpenReadMulti(path, fileSystem);
			byte[] array = new byte[smartStream.Length];
			smartStream.Read(array, 0, array.Length);
			fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(array, MultiFileStream.GetFilePath(path), MultiFileStream.GetFileName(path), dependencyProvider));
		}
	}

	private static void LoadVfsBlockInfo(string blockInfoPath, List<FileBase> fileStack, IDependencyProvider? dependencyProvider)
	{
		if (!EndField_0_8_25_Vfs.TryParseBlockInfo(blockInfoPath, out EndFieldVfsBlock block, out string error))
		{
			Logger.Warning("[EndField] Failed to parse VFS block info " + Path.GetFileName(blockInfoPath) + ": " + error);
			return;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (EndFieldVfsChunk chunk in block.Chunks)
		{
			foreach (EndFieldVfsFile file in chunk.Files)
			{
				if (!EndField_0_8_25_Vfs.TryReadFile(block, chunk, file, out byte[] data, out string error2))
				{
					num3++;
					Logger.Warning("[EndField] Failed to read VFS file " + file.FileName + ": " + error2);
					continue;
				}
				List<FileBase> list = GameBundleHook.LoadFilesAndDependencies(data, MultiFileStream.GetFilePath(blockInfoPath), file.FileName, dependencyProvider);
				if (!list.Any((FileBase loadedFile) => !(loadedFile is ResourceFile) && !(loadedFile is FailedFile)))
				{
					num2++;
					continue;
				}
				foreach (FileBase item in list)
				{
					if (!(item is FailedFile))
					{
						fileStack.Add(item);
					}
				}
				num++;
			}
		}
		Logger.Info($"[EndField] VFS {block.GroupName}: loaded {num}, skipped {num2}, failed {num3}, chunks {block.Chunks.Count}, files {block.GroupFileCount}");
	}

	public static bool CustomAssetBundlesCheck(string filePath)
	{
		if (EndField_0_8_25_Vfs.IsVfsPath(filePath))
		{
			return EndField_0_8_25_Vfs.IsBlockInfoPath(filePath);
		}
		if (!filePath.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
		{
			return filePath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool CustomAssetBundlesCheckMagicNum(EndianReader reader, MethodInfo FromSerializedFile)
	{
		if (IsUnityBundleHeader(reader, FromSerializedFile, "UnityFS") || IsUnityBundleHeader(reader, FromSerializedFile, "UnityWeb") || IsUnityBundleHeader(reader, FromSerializedFile, "UnityRaw"))
		{
			return true;
		}
		if (IsCurrentVfsBlockInfoHeader(reader))
		{
			return true;
		}
		return VFSDecryptor.IsValidHeader(reader);
	}

	private static bool IsUnityBundleHeader(EndianReader reader, MethodInfo fromSerializedFile, string signature)
	{
		long position = reader.BaseStream.Position;
		EndianType endianType = reader.EndianType;
		try
		{
			return (bool)(fromSerializedFile.Invoke(null, new object[2] { reader, signature }) ?? ((object)false));
		}
		finally
		{
			reader.BaseStream.Position = position;
			reader.EndianType = endianType;
		}
	}

	private static bool IsCurrentVfsBlockInfoHeader(EndianReader reader)
	{
		long position = reader.BaseStream.Position;
		EndianType endianType = reader.EndianType;
		try
		{
			if (reader.BaseStream.Length - position < 4)
			{
				return false;
			}
			reader.EndianType = EndianType.LittleEndian;
			return reader.ReadInt32() == 3;
		}
		finally
		{
			reader.BaseStream.Position = position;
			reader.EndianType = endianType;
		}
	}

	protected EndField_0_8_25_Hook()
	{
		vfsDecryptor = new VFSDecryptor(vfsBundleVariant);
	}

	protected override UnityVersion GetTargetVersion(RipperHookAttribute attr)
	{
		return endFieldClassVersion;
	}

	protected override void InitAttributeHook()
	{
		RegisterModule(new GameBundleHook(CustomFilePreInitialize));
		RegisterModule(new PlatformGameStructureHook_CollectAssetBundles(CustomAssetBundlesCheck));
		RegisterModule(new PlatformGameStructureHook_IsBundleHeader(CustomAssetBundlesCheckMagicNum));
		RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
		base.InitAttributeHook();
	}

	public static void SetPrivateProperty(object instance, string propertyName, object value)
	{
		Type type = instance.GetType();
		PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property == null)
		{
			throw new Exception("Property " + propertyName + " not found on " + type.FullName);
		}
		MethodInfo setMethod = property.GetSetMethod(nonPublic: true);
		if (setMethod == null)
		{
			throw new Exception("Property " + propertyName + " has no setter on " + type.FullName);
		}
		setMethod.Invoke(instance, new object[1] { value });
	}
}
