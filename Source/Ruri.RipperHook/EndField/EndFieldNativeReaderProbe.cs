using System.Reflection.PortableExecutable;
using System.Text;

namespace Ruri.RipperHook.EndField;

public static class EndFieldNativeReaderProbe
{
    private static readonly string[] NativeNeedles =
    [
        "StreamingSceneV2",
        "StreamingSceneNative_Create",
        "StreamingScene",
        "FlatBuffer",
        "VFS",
        ".bytes",
        "ChunkData_",
        "StringPathHash.bin",
        "InitStringPathHash.bin",
        "index_main",
        "archive:/CAB-",
        ".chk",
        ".blc",
    ];

    private static readonly string[] MetadataNeedles =
    [
        "StreamingScene",
        "HyperGryph.Streaming",
        "FlatBuffer",
        "VFS",
    ];

    private static readonly string[] ReaderNeedles =
    [
        "CreateStreamingScene",
        "CreateDynamicStreamingScene",
        "DestroyDynamicStreamingScene",
        "GetStreamingScene",
        "GetDynamicStreamingScene",
        "FlushStreamingSceneState",
        "StreamingSceneConfig",
        "Beyond_VFS_EFileLoaderPosType",
        "Beyond_VFS_EVFSBlockType",
    ];

    private static readonly string[] NativeBindingNeedles =
    [
        "UnityEngine.HyperGryph.HGResourceManager::get_usingVFS",
        "UnityEngine.HyperGryph.HGResourceManager::LoadAsync_Injected",
        "UnityEngine.HyperGryph.Streaming.FlatBufferConvertContextV2::TryEnqueueAssetForAsyncLoad_Injected",
        "UnityEngine.HyperGryph.Streaming.StreamingSceneV2::Create_Injected",
        "UnityEngine.HyperGryph.Streaming.StreamingSceneV2::Destroy_Injected",
        "UnityEngine.HyperGryph.Streaming.StreamingSceneV2::SetSceneState_Injected",
        "UnityEngine.HyperGryph.Streaming.StreamingSceneV2::QueryChunkLoadStatusImpl_Injected",
    ];

    private static readonly string[] ToolNames =
    [
        "Il2CppDumper.exe",
        "Cpp2IL.exe",
    ];

    public static IReadOnlyList<string> Analyze(string gameDataDirectory)
    {
        List<string> lines = new();
        string normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
        string? gameRoot = Directory.GetParent(normalizedGameDataDirectory)?.FullName;
        string gameAssembly = gameRoot is null ? string.Empty : Path.Combine(gameRoot, "GameAssembly.dll");
        string unityPlayer = gameRoot is null ? string.Empty : Path.Combine(gameRoot, "UnityPlayer.dll");
        string metadata = Path.Combine(normalizedGameDataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat");

        lines.Add($"native reader probe: gameData={normalizedGameDataDirectory}");
        lines.Add($"native reader probe: gameAssembly={File.Exists(gameAssembly)} unityPlayer={File.Exists(unityPlayer)} metadata={File.Exists(metadata)}");

        bool foundNativeCandidate = false;
        if (File.Exists(gameAssembly))
        {
            foundNativeCandidate = AddNativeCandidates(lines, gameAssembly);
        }

        if (File.Exists(unityPlayer))
        {
            foundNativeCandidate |= AddNativeCandidates(lines, unityPlayer);
        }

        bool foundMetadataCandidate = false;
        if (File.Exists(metadata))
        {
            foundMetadataCandidate = AddMetadataCandidates(lines, metadata);
            AddReaderCandidates(lines, metadata);
        }

        bool foundTool = AddExternalToolCandidates(lines);
        if (foundNativeCandidate && foundMetadataCandidate)
        {
            lines.Add("stop reason=candidate-found");
        }
        else if (!foundTool)
        {
            lines.Add("stop reason=missing-tool");
        }
        else
        {
            lines.Add("stop reason=no-xref");
        }

        return lines;
    }

    private static bool AddNativeCandidates(List<string> lines, string gameAssembly)
    {
        byte[] bytes = File.ReadAllBytes(gameAssembly);
        using MemoryStream stream = new(bytes, writable: false);
        using PEReader reader = new(stream);
        ulong imageBase = reader.PEHeaders.PEHeader?.ImageBase ?? 0;
        IReadOnlyList<SectionInfo> sections = reader.PEHeaders.SectionHeaders
            .Select(static header => new SectionInfo(
                header.PointerToRawData,
                header.SizeOfRawData,
                header.VirtualAddress,
                header.VirtualSize,
                header.Name,
                header.SectionCharacteristics))
            .ToArray();

        bool found = false;
        List<NativeStringHit> stringHits = new();
        foreach (string needle in NativeNeedles)
        {
            int hitIndex = 0;
            foreach (long fileOffset in FindAll(bytes, Encoding.ASCII.GetBytes(needle)).Take(128))
            {
                int rva = FileOffsetToRva(sections, fileOffset);
                string section = FindSection(sections, fileOffset);
                if (hitIndex < 12)
                {
                    lines.Add($"native candidate module={Path.GetFileName(gameAssembly)} string={needle} fileOffset=0x{fileOffset:X} rva=0x{rva:X} section={section}");
                }

                if (rva >= 0)
                {
                    stringHits.Add(new NativeStringHit(needle, fileOffset, rva));
                }

                found = true;
                hitIndex++;
            }

            if (hitIndex > 12)
            {
                lines.Add($"native candidate module={Path.GetFileName(gameAssembly)} string={needle} omitted={hitIndex - 12}");
            }
        }

        string moduleName = Path.GetFileName(gameAssembly);
        AddNativeXrefCandidates(lines, bytes, sections, stringHits, imageBase, moduleName);
        IReadOnlyList<BindingCandidate> bindings = AddNativeBindingCandidates(lines, bytes, sections, imageBase, moduleName);
        AddNativeCallCandidates(lines, bytes, sections, moduleName, bindings);

        if (!found)
        {
            lines.Add("native candidate none");
        }

        return found;
    }

    private static IReadOnlyList<BindingCandidate> AddNativeBindingCandidates(
        List<string> lines,
        byte[] bytes,
        IReadOnlyList<SectionInfo> sections,
        ulong imageBase,
        string moduleName)
    {
        List<BindingCandidate> bindings = new();
        Dictionary<string, int> namePointerOffsets = new(StringComparer.Ordinal);
        foreach (string needle in NativeBindingNeedles)
        {
            long stringFileOffset = FindAll(bytes, Encoding.ASCII.GetBytes(needle)).FirstOrDefault(-1);
            if (stringFileOffset < 0)
            {
                continue;
            }

            int stringRva = FileOffsetToRva(sections, stringFileOffset);
            if (stringRva < 0 || !TryFindPointerToRva(bytes, sections, imageBase, stringRva, out int pointerFileOffset))
            {
                continue;
            }

            namePointerOffsets[needle] = pointerFileOffset;
        }

        if (namePointerOffsets.Count == 0)
        {
            return bindings;
        }

        int anchorPointerOffset = namePointerOffsets.Values.Min();
        int nameTableStart = anchorPointerOffset;
        while (nameTableStart - sizeof(ulong) >= 0 && PointsToAsciiString(bytes, sections, imageBase, nameTableStart - sizeof(ulong)))
        {
            nameTableStart -= sizeof(ulong);
        }

        int nameTableEnd = anchorPointerOffset;
        while (nameTableEnd + sizeof(ulong) < bytes.Length && PointsToAsciiString(bytes, sections, imageBase, nameTableEnd + sizeof(ulong)))
        {
            nameTableEnd += sizeof(ulong);
        }

        int functionTableStart = nameTableEnd + sizeof(ulong);
        int nameCount = ((nameTableEnd - nameTableStart) / sizeof(ulong)) + 1;
        lines.Add($"native binding table module={moduleName} namesFile=0x{nameTableStart:X} funcsFile=0x{functionTableStart:X} names={nameCount}");

        foreach ((string name, int pointerFileOffset) in namePointerOffsets.OrderBy(static pair => pair.Value))
        {
            int index = (pointerFileOffset - nameTableStart) / sizeof(ulong);
            int functionPointerFileOffset = functionTableStart + index * sizeof(ulong);
            if (functionPointerFileOffset < 0 || functionPointerFileOffset > bytes.Length - sizeof(ulong))
            {
                continue;
            }

            ulong functionVa = BitConverter.ToUInt64(bytes, functionPointerFileOffset);
            if (!TryVaToRva(imageBase, functionVa, out int functionRva))
            {
                continue;
            }

            string functionSection = FindSectionByRva(sections, functionRva);
            if (functionSection != ".text")
            {
                continue;
            }

            lines.Add($"reader candidate function={moduleName}+0x{functionRva:X} evidence=binding:{name} index={index} namePtrFile=0x{pointerFileOffset:X} funcPtrFile=0x{functionPointerFileOffset:X}");
            bindings.Add(new BindingCandidate(name, functionRva, functionPointerFileOffset, index));
        }

        return bindings;
    }

    private static void AddNativeCallCandidates(
        List<string> lines,
        byte[] bytes,
        IReadOnlyList<SectionInfo> sections,
        string moduleName,
        IReadOnlyList<BindingCandidate> bindings)
    {
        if (bindings.Count == 0)
        {
            return;
        }

        lines.Add($"native call scan module={moduleName} mode=opcode-window note=raw-candidates-require-disassembler-confirmation");

        HashSet<int> followUpTargets = new();
        foreach (BindingCandidate binding in bindings)
        {
            if (!TryGetPrimaryCallScanLength(binding.Name, out int scanLength))
            {
                continue;
            }

            foreach (DirectCallHit call in FindDirectCalls(bytes, sections, binding.FunctionRva, scanLength).Take(12))
            {
                lines.Add($"native call module={moduleName} source={ShortBindingName(binding.Name)} sourceRva=0x{binding.FunctionRva:X} callRva=0x{call.CallRva:X} kind=raw-{call.Kind} target={moduleName}+0x{call.TargetRva:X}");
                if (binding.Name.Contains("StreamingSceneV2::Create_Injected", StringComparison.Ordinal) && call.TargetRva == 0x10BD3C0)
                {
                    followUpTargets.Add(call.TargetRva);
                }
            }
        }

        foreach (int targetRva in followUpTargets.Order())
        {
            foreach (DirectCallHit call in FindDirectCalls(bytes, sections, targetRva, 0x240).Take(12))
            {
                lines.Add($"native call module={moduleName} source={moduleName}+0x{targetRva:X} sourceRva=0x{targetRva:X} callRva=0x{call.CallRva:X} kind=raw-{call.Kind} target={moduleName}+0x{call.TargetRva:X}");
            }
        }
    }

    private static bool TryGetPrimaryCallScanLength(string name, out int scanLength)
    {
        if (name.Contains("HGResourceManager::LoadAsync_Injected", StringComparison.Ordinal))
        {
            scanLength = 0x120;
            return true;
        }

        if (name.Contains("FlatBufferConvertContextV2::TryEnqueueAssetForAsyncLoad_Injected", StringComparison.Ordinal))
        {
            scanLength = 0x180;
            return true;
        }

        if (name.Contains("StreamingSceneV2::Create_Injected", StringComparison.Ordinal))
        {
            scanLength = 0x1A0;
            return true;
        }

        if (name.Contains("StreamingSceneV2::QueryChunkLoadStatusImpl_Injected", StringComparison.Ordinal))
        {
            scanLength = 0xE0;
            return true;
        }

        scanLength = 0;
        return false;
    }

    private static string ShortBindingName(string name)
    {
        int namespaceSeparator = name.LastIndexOf('.');
        return namespaceSeparator >= 0 ? name[(namespaceSeparator + 1)..] : name;
    }

    private static IEnumerable<DirectCallHit> FindDirectCalls(byte[] bytes, IReadOnlyList<SectionInfo> sections, int functionRva, int scanLength)
    {
        int fileOffset = RvaToFileOffset(sections, functionRva);
        if (fileOffset < 0)
        {
            yield break;
        }

        int end = Math.Min(bytes.Length, fileOffset + scanLength);
        SectionInfo? executableSection = sections.FirstOrDefault(section => section.IsExecutable && functionRva >= section.VirtualAddress && functionRva < section.VirtualAddress + Math.Max(section.VirtualSize, section.RawSize));
        if (executableSection is null)
        {
            yield break;
        }

        for (int offset = fileOffset; offset <= end - 5; offset++)
        {
            byte opcode = bytes[offset];
            if (opcode is not (0xE8 or 0xE9))
            {
                continue;
            }

            int instructionRva = executableSection.VirtualAddress + (offset - executableSection.RawStart);
            int relative = BitConverter.ToInt32(bytes, offset + 1);
            int targetRva = instructionRva + 5 + relative;
            if (FindSectionByRva(sections, targetRva) != ".text")
            {
                continue;
            }

            yield return new DirectCallHit(instructionRva, opcode == 0xE8 ? "call" : "jmp", targetRva);
        }
    }

    private static void AddNativeXrefCandidates(
        List<string> lines,
        byte[] bytes,
        IReadOnlyList<SectionInfo> sections,
        IReadOnlyList<NativeStringHit> stringHits,
        ulong imageBase,
        string moduleName)
    {
        if (stringHits.Count == 0)
        {
            lines.Add($"native xref module={moduleName} none reason=no-native-strings");
            return;
        }

        Dictionary<int, List<string>> stringsByRva = new();
        foreach (NativeStringHit hit in stringHits)
        {
            if (!stringsByRva.TryGetValue(hit.Rva, out List<string>? names))
            {
                names = new List<string>();
                stringsByRva.Add(hit.Rva, names);
            }

            if (!names.Contains(hit.Needle, StringComparer.Ordinal))
            {
                names.Add(hit.Needle);
            }
        }

        List<XrefHit> xrefs = FindRipRelativeXrefs(bytes, sections, stringsByRva, viaPointer: false);

        Dictionary<int, List<string>> pointerTargets = FindAbsolutePointerTargets(bytes, sections, stringsByRva, imageBase);
        if (pointerTargets.Count > 0)
        {
            xrefs.AddRange(FindRipRelativeXrefs(bytes, sections, pointerTargets, viaPointer: true));
        }

        if (xrefs.Count == 0)
        {
            lines.Add($"native xref module={moduleName} none reason=no-rip-relative-reference");
            return;
        }

        foreach (XrefHit xref in xrefs
            .OrderBy(static xref => xref.StringName, StringComparer.Ordinal)
            .ThenBy(static xref => xref.CodeRva)
            .Take(48))
        {
            string via = xref.ViaPointer ? " via=absolute-pointer" : string.Empty;
            lines.Add($"native xref module={moduleName} string={xref.StringName} targetRva=0x{xref.TargetRva:X} codeRva=0x{xref.CodeRva:X} dispRva=0x{xref.DisplacementRva:X} section={xref.Section}{via}");
            lines.Add($"reader candidate function={moduleName}+0x{xref.CodeRva:X} evidence=xref:{xref.StringName}@0x{xref.TargetRva:X}");
        }
    }

    private static List<XrefHit> FindRipRelativeXrefs(
        byte[] bytes,
        IReadOnlyList<SectionInfo> sections,
        IReadOnlyDictionary<int, List<string>> targetsByRva,
        bool viaPointer)
    {
        List<XrefHit> xrefs = new();
        foreach (SectionInfo section in sections.Where(static section => section.IsExecutable))
        {
            int rawStart = section.RawStart;
            int rawEnd = Math.Min(bytes.Length, section.RawStart + section.RawSize);
            for (int offset = rawStart; offset <= rawEnd - 7; offset++)
            {
                if (!TryReadRipRelativeInstruction(bytes, section, offset, out int instructionRva, out int displacementRva, out int targetRva))
                {
                    continue;
                }

                if (!targetsByRva.TryGetValue(targetRva, out List<string>? names))
                {
                    continue;
                }

                foreach (string name in names)
                {
                    xrefs.Add(new XrefHit(name, targetRva, instructionRva, displacementRva, section.Name, viaPointer));
                }
            }
        }

        return xrefs;
    }

    private static bool TryReadRipRelativeInstruction(
        byte[] bytes,
        SectionInfo section,
        int instructionOffset,
        out int instructionRva,
        out int displacementRva,
        out int targetRva)
    {
        instructionRva = section.VirtualAddress + (instructionOffset - section.RawStart);
        displacementRva = 0;
        targetRva = 0;

        int cursor = instructionOffset;
        byte first = bytes[cursor];
        if (first is >= 0x40 and <= 0x4F)
        {
            cursor++;
        }

        byte opcode = bytes[cursor++];
        if (opcode is not (0x8D or 0x8B or 0x89 or 0xC7))
        {
            return false;
        }

        byte modRm = bytes[cursor++];
        if ((modRm & 0xC7) != 0x05)
        {
            return false;
        }

        displacementRva = section.VirtualAddress + (cursor - section.RawStart);
        int displacement = BitConverter.ToInt32(bytes, cursor);
        targetRva = displacementRva + sizeof(int) + displacement;
        return true;
    }

    private static Dictionary<int, List<string>> FindAbsolutePointerTargets(
        byte[] bytes,
        IReadOnlyList<SectionInfo> sections,
        IReadOnlyDictionary<int, List<string>> stringsByRva,
        ulong imageBase)
    {
        Dictionary<int, List<string>> pointerTargets = new();
        if (imageBase == 0)
        {
            return pointerTargets;
        }

        foreach (SectionInfo section in sections.Where(static section => !section.IsExecutable))
        {
            int rawStart = section.RawStart;
            int rawEnd = Math.Min(bytes.Length, section.RawStart + section.RawSize);
            for (int offset = rawStart; offset <= rawEnd - sizeof(ulong); offset += sizeof(ulong))
            {
                ulong value = BitConverter.ToUInt64(bytes, offset);
                if (value < imageBase)
                {
                    continue;
                }

                ulong relative = value - imageBase;
                if (relative > int.MaxValue)
                {
                    continue;
                }

                int targetRva = (int)relative;
                if (!stringsByRva.TryGetValue(targetRva, out List<string>? names))
                {
                    continue;
                }

                int pointerRva = section.VirtualAddress + (offset - section.RawStart);
                if (!pointerTargets.TryGetValue(pointerRva, out List<string>? pointerNames))
                {
                    pointerNames = new List<string>();
                    pointerTargets.Add(pointerRva, pointerNames);
                }

                foreach (string name in names)
                {
                    if (!pointerNames.Contains(name, StringComparer.Ordinal))
                    {
                        pointerNames.Add(name);
                    }
                }
            }
        }

        return pointerTargets;
    }

    private static bool TryFindPointerToRva(byte[] bytes, IReadOnlyList<SectionInfo> sections, ulong imageBase, int rva, out int pointerFileOffset)
    {
        pointerFileOffset = -1;
        ulong target = imageBase + (uint)rva;
        foreach (SectionInfo section in sections.Where(static section => !section.IsExecutable))
        {
            int rawStart = section.RawStart;
            int rawEnd = Math.Min(bytes.Length, section.RawStart + section.RawSize);
            for (int offset = rawStart; offset <= rawEnd - sizeof(ulong); offset += sizeof(ulong))
            {
                if (BitConverter.ToUInt64(bytes, offset) == target)
                {
                    pointerFileOffset = offset;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PointsToAsciiString(byte[] bytes, IReadOnlyList<SectionInfo> sections, ulong imageBase, int pointerFileOffset)
    {
        if (pointerFileOffset < 0 || pointerFileOffset > bytes.Length - sizeof(ulong))
        {
            return false;
        }

        ulong value = BitConverter.ToUInt64(bytes, pointerFileOffset);
        if (!TryVaToRva(imageBase, value, out int rva))
        {
            return false;
        }

        int stringFileOffset = RvaToFileOffset(sections, rva);
        if (stringFileOffset < 0 || stringFileOffset > bytes.Length - 4)
        {
            return false;
        }

        return bytes[stringFileOffset] is >= 0x20 and <= 0x7E
            && bytes[stringFileOffset + 1] is >= 0x20 and <= 0x7E
            && bytes[stringFileOffset + 2] is >= 0x20 and <= 0x7E
            && bytes[stringFileOffset + 3] is >= 0x20 and <= 0x7E;
    }

    private static bool TryVaToRva(ulong imageBase, ulong value, out int rva)
    {
        rva = -1;
        if (imageBase == 0 || value < imageBase)
        {
            return false;
        }

        ulong relative = value - imageBase;
        if (relative > int.MaxValue)
        {
            return false;
        }

        rva = (int)relative;
        return true;
    }

    private static bool AddMetadataCandidates(List<string> lines, string metadata)
    {
        byte[] bytes = File.ReadAllBytes(metadata);
        bool found = false;
        foreach (string needle in MetadataNeedles)
        {
            foreach (long offset in FindAll(bytes, Encoding.UTF8.GetBytes(needle)).Take(12))
            {
                string context = ExtractAsciiContext(bytes, offset, 96);
                lines.Add($"metadata candidate needle={needle} offset=0x{offset:X} context={context}");
                found = true;
            }
        }

        if (!found)
        {
            lines.Add("metadata candidate none");
        }

        return found;
    }

    private static void AddReaderCandidates(List<string> lines, string metadata)
    {
        byte[] bytes = File.ReadAllBytes(metadata);
        bool found = false;
        foreach (string needle in ReaderNeedles)
        {
            foreach (long offset in FindAll(bytes, Encoding.UTF8.GetBytes(needle)).Take(8))
            {
                string context = ExtractAsciiContext(bytes, offset, 96);
                lines.Add($"reader candidate function={needle} address=metadata:0x{offset:X} evidence={context}");
                found = true;
            }
        }

        if (!found)
        {
            lines.Add("reader candidate function=none evidence=metadata-scan");
        }
    }

    private static bool AddExternalToolCandidates(List<string> lines)
    {
        bool found = false;
        foreach (string toolName in ToolNames)
        {
            string? tool = FindTool(toolName);
            if (tool is not null)
            {
                lines.Add($"external tool found name={toolName} path={tool}");
                found = true;
            }
            else
            {
                lines.Add($"external tool missing name={toolName}");
            }
        }

        if (!found)
        {
            lines.Add("reader candidate function=unavailable evidence=external-il2cpp-tool-missing");
        }

        return found;
    }

    private static IEnumerable<long> FindAll(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            yield break;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                yield return i;
            }
        }
    }

    private static int FileOffsetToRva(IReadOnlyList<SectionInfo> sections, long fileOffset)
    {
        foreach (SectionInfo section in sections)
        {
            if (fileOffset >= section.RawStart && fileOffset < section.RawStart + section.RawSize)
            {
                return checked((int)(section.VirtualAddress + (fileOffset - section.RawStart)));
            }
        }

        return -1;
    }

    private static int RvaToFileOffset(IReadOnlyList<SectionInfo> sections, int rva)
    {
        foreach (SectionInfo section in sections)
        {
            int virtualSize = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + virtualSize)
            {
                return section.RawStart + (rva - section.VirtualAddress);
            }
        }

        return -1;
    }

    private static string FindSection(IReadOnlyList<SectionInfo> sections, long fileOffset)
    {
        foreach (SectionInfo section in sections)
        {
            if (fileOffset >= section.RawStart && fileOffset < section.RawStart + section.RawSize)
            {
                return section.Name;
            }
        }

        return "(none)";
    }

    private static string FindSectionByRva(IReadOnlyList<SectionInfo> sections, int rva)
    {
        foreach (SectionInfo section in sections)
        {
            int virtualSize = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + virtualSize)
            {
                return section.Name;
            }
        }

        return "(none)";
    }

    private static string ExtractAsciiContext(byte[] bytes, long offset, int radius)
    {
        int start = (int)Math.Max(0, offset - radius);
        int end = (int)Math.Min(bytes.Length, offset + radius);
        StringBuilder builder = new(end - start);
        for (int i = start; i < end; i++)
        {
            byte value = bytes[i];
            builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        return builder.ToString().ReplaceLineEndings(" ");
    }

    private static string? FindTool(string executableName)
    {
        foreach (string directory in GetToolSearchRoots())
        {
            string candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string toolsRoot = Path.Combine("E:", "RuriWorks", "tools");
        if (Directory.Exists(toolsRoot))
        {
            string? recursiveHit = Directory.EnumerateFiles(toolsRoot, executableName, SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (recursiveHit is not null)
            {
                return recursiveHit;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetToolSearchRoots()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    yield return directory;
                }
            }
        }

        string? ruriWorksTools = Environment.GetEnvironmentVariable("RURIWORKS_TOOLS");
        if (!string.IsNullOrWhiteSpace(ruriWorksTools))
        {
            yield return ruriWorksTools;
        }
    }

    private sealed record SectionInfo(
        int RawStart,
        int RawSize,
        int VirtualAddress,
        int VirtualSize,
        string Name,
        SectionCharacteristics Characteristics)
    {
        public bool IsExecutable => Characteristics.HasFlag(SectionCharacteristics.MemExecute);
    }

    private sealed record NativeStringHit(string Needle, long FileOffset, int Rva);

    private sealed record XrefHit(string StringName, int TargetRva, int CodeRva, int DisplacementRva, string Section, bool ViaPointer);

    private sealed record BindingCandidate(string Name, int FunctionRva, int FunctionPointerFileOffset, int Index);

    private sealed record DirectCallHit(int CallRva, string Kind, int TargetRva);
}
