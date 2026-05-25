using System.Diagnostics;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.Processing.Configuration;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ruri.RipperHook.AR;
using Ruri.RipperHook.Endfield;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.CLI;

internal static class VfsCliRunner
{
    private const string DeadbeefGuid = "0000000deadbeef15deadf00d0000000";
    private static readonly Regex CabNamePattern = new(@"\bCAB[-_]([0-9a-fA-F]{32})\b|\bcab[-_]([0-9a-fA-F]{32})\b", RegexOptions.Compiled);
    private static readonly Regex ShaderLinePattern = new(@"^(?<prefix>\s*m_Shader:\s*)\{fileID:\s*4800000,\s*guid:\s*[0-9a-fA-F]{32},\s*type:\s*\d+\}\s*$", RegexOptions.Compiled);

    public static bool ShouldRun(CliOptions options)
        => options.BuildVfsIndex
           || options.ProbeVfsMetadata
           || !string.IsNullOrWhiteSpace(options.ScanVfsTerms)
           || !string.IsNullOrWhiteSpace(options.VfsDeps)
           || !string.IsNullOrWhiteSpace(options.LoadLogical)
           || !string.IsNullOrWhiteSpace(options.RepairUnityMaterials);

    public static int Run(CliOptions options)
    {
        ConfigureLogging(options);

        try
        {
            if (options.BuildVfsIndex)
            {
                return BuildVfsIndex(options);
            }

            if (options.ProbeVfsMetadata)
            {
                return ProbeVfsMetadata(options);
            }

            if (!string.IsNullOrWhiteSpace(options.ScanVfsTerms))
            {
                return ScanVfsTerms(options);
            }

            if (!string.IsNullOrWhiteSpace(options.VfsDeps))
            {
                return WriteVfsClosure(options, options.VfsDeps!);
            }

            if (!string.IsNullOrWhiteSpace(options.LoadLogical))
            {
                return LoadLogical(options);
            }

            if (!string.IsNullOrWhiteSpace(options.RepairUnityMaterials))
            {
                return InspectUnityMaterials(options);
            }

            Emit(new { status = "error", error = "No VFS command selected." });
            return 1;
        }
        catch (Exception ex)
        {
            Emit(new
            {
                status = "error",
                error = $"{ex.GetType().Name}: {ex.Message}",
                stack = ex.ToString(),
            });
            return 1;
        }
    }

    private static int BuildVfsIndex(CliOptions options)
    {
        string gameRoot = RequireExistingDirectory(options.GameRoot, "--game-root");
        string dbPath = RequirePath(options.VfsDbPath, "--vfs-db");
        string outputRoot = GetOutputRoot(options, dbPath);

        Directory.CreateDirectory(outputRoot);
        using SqliteConnection connection = OpenDb(dbPath);
        EnsureSchema(connection);

        Execute(connection, "DELETE FROM logical_files;");
        Execute(connection, "DELETE FROM payloads;");
        Execute(connection, "DELETE FROM block_parse_errors;");
        Execute(connection, "DELETE FROM term_hits;");

        int sequence = 0;
        int logicalCount = 0;
        int abCount = 0;
        int parseErrors = 0;
        Dictionary<string, int> sourceCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> extensionCounts = new(StringComparer.OrdinalIgnoreCase);

        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand insertLogical = connection.CreateCommand();
        insertLogical.Transaction = tx;
        insertLogical.CommandText = """
            INSERT INTO logical_files
            (sequence, logical_path, source_root, block_info_path, group_name, chunk_md5_name, chunk_path, chunk_exists, offset, length, file_data_md5, extension, block_type, use_encrypt, payload_id)
            VALUES ($sequence, $logical_path, $source_root, $block_info_path, $group_name, $chunk_md5_name, $chunk_path, $chunk_exists, $offset, $length, $file_data_md5, $extension, $block_type, $use_encrypt, $payload_id);
            """;
        AddParams(insertLogical, "$sequence", "$logical_path", "$source_root", "$block_info_path", "$group_name", "$chunk_md5_name", "$chunk_path", "$chunk_exists", "$offset", "$length", "$file_data_md5", "$extension", "$block_type", "$use_encrypt", "$payload_id");

        using SqliteCommand insertPayload = connection.CreateCommand();
        insertPayload.Transaction = tx;
        insertPayload.CommandText = """
            INSERT OR IGNORE INTO payloads (payload_id, length, file_data_md5)
            VALUES ($payload_id, $length, $file_data_md5);
            """;
        AddParams(insertPayload, "$payload_id", "$length", "$file_data_md5");

        using SqliteCommand insertError = connection.CreateCommand();
        insertError.Transaction = tx;
        insertError.CommandText = "INSERT INTO block_parse_errors (block_info_path, error) VALUES ($path, $error);";
        AddParams(insertError, "$path", "$error");

        foreach (string blockInfoPath in Directory.EnumerateFiles(gameRoot, "*.blc", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!EndField_0_8_25_Vfs.IsBlockInfoPath(blockInfoPath))
            {
                continue;
            }

            if (!EndField_0_8_25_Vfs.TryParseBlockInfo(blockInfoPath, out EndFieldVfsBlock block, out string error))
            {
                insertError.Parameters["$path"].Value = blockInfoPath;
                insertError.Parameters["$error"].Value = error;
                insertError.ExecuteNonQuery();
                parseErrors++;
                continue;
            }

            string sourceRoot = DetectSourceRoot(blockInfoPath);
            sourceCounts[sourceRoot] = sourceCounts.GetValueOrDefault(sourceRoot) + block.GroupFileCount;

            foreach (EndFieldVfsChunk chunk in block.Chunks)
            {
                string chunkPath = Path.Combine(Path.GetDirectoryName(blockInfoPath) ?? string.Empty, $"{chunk.Md5Name}.chk");
                int chunkExists = File.Exists(chunkPath) ? 1 : 0;

                foreach (EndFieldVfsFile file in chunk.Files)
                {
                    sequence++;
                    logicalCount++;

                    string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (string.IsNullOrEmpty(extension))
                    {
                        extension = "<none>";
                    }

                    extensionCounts[extension] = extensionCounts.GetValueOrDefault(extension) + 1;
                    if (extension.Equals(".ab", StringComparison.OrdinalIgnoreCase))
                    {
                        abCount++;
                    }

                    string payloadId = MakePayloadId(file.Length, file.FileDataMd5);

                    insertLogical.Parameters["$sequence"].Value = sequence;
                    insertLogical.Parameters["$logical_path"].Value = NormalizeLogicalPath(file.FileName);
                    insertLogical.Parameters["$source_root"].Value = sourceRoot;
                    insertLogical.Parameters["$block_info_path"].Value = blockInfoPath;
                    insertLogical.Parameters["$group_name"].Value = block.GroupName;
                    insertLogical.Parameters["$chunk_md5_name"].Value = chunk.Md5Name;
                    insertLogical.Parameters["$chunk_path"].Value = chunkPath;
                    insertLogical.Parameters["$chunk_exists"].Value = chunkExists;
                    insertLogical.Parameters["$offset"].Value = checked((long)file.Offset);
                    insertLogical.Parameters["$length"].Value = checked((long)file.Length);
                    insertLogical.Parameters["$file_data_md5"].Value = file.FileDataMd5;
                    insertLogical.Parameters["$extension"].Value = extension;
                    insertLogical.Parameters["$block_type"].Value = (int)file.BlockType;
                    insertLogical.Parameters["$use_encrypt"].Value = file.UseEncrypt ? 1 : 0;
                    insertLogical.Parameters["$payload_id"].Value = payloadId;
                    insertLogical.ExecuteNonQuery();

                    insertPayload.Parameters["$payload_id"].Value = payloadId;
                    insertPayload.Parameters["$length"].Value = checked((long)file.Length);
                    insertPayload.Parameters["$file_data_md5"].Value = file.FileDataMd5;
                    insertPayload.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();

        Execute(connection, """
            UPDATE payloads
            SET alias_count = (
                    SELECT COUNT(*) FROM logical_files lf WHERE lf.payload_id = payloads.payload_id
                ),
                preferred_logical_id = (
                    SELECT logical_id
                    FROM logical_files lf
                    WHERE lf.payload_id = payloads.payload_id
                    ORDER BY lf.chunk_exists DESC,
                             CASE lf.source_root WHEN 'Persistent' THEN 0 WHEN 'StreamingAssets' THEN 1 ELSE 2 END,
                             lf.logical_id
                    LIMIT 1
                );
            """);

        long uniqueAbCount = ScalarLong(connection, "SELECT COUNT(DISTINCT payload_id) FROM logical_files WHERE extension = '.ab';");
        long duplicateAbCount = abCount - uniqueAbCount;

        WriteIndexReports(outputRoot, gameRoot, logicalCount, abCount, uniqueAbCount, duplicateAbCount, parseErrors, sourceCounts, extensionCounts, connection);
        Emit(new
        {
            status = "ok",
            command = "build-vfs-index",
            db = dbPath,
            output_root = outputRoot,
            logical_files = logicalCount,
            logical_ab = abCount,
            unique_ab = uniqueAbCount,
            duplicate_ab = duplicateAbCount,
            parse_errors = parseErrors,
        });
        return 0;
    }

    private static int ProbeVfsMetadata(CliOptions options)
    {
        string dbPath = RequirePath(options.VfsDbPath, "--vfs-db");
        string outputRoot = GetOutputRoot(options, dbPath);
        Directory.CreateDirectory(outputRoot);

        ShardSpec shard = ShardSpec.Parse(options.Shard);
        using SqliteConnection connection = OpenDb(dbPath);
        EnsureSchema(connection);

        List<LogicalSlice> payloads = options.ProbeVfsHitMetadata
            ? LoadTermHitAbPayloads(connection)
            : LoadPreferredAbPayloads(connection);
        if (shard.Total > 1)
        {
            payloads = payloads.Where((_, index) => index % shard.Total == shard.Index).ToList();
        }

        int attempted = 0;
        int skipped = 0;
        int loaded = 0;
        int failed = 0;

        string tempRoot = Path.Combine(outputRoot, "cache", "vfs_probe", $"{Environment.ProcessId}_{shard.Index}_{shard.Total}");
        Directory.CreateDirectory(tempRoot);

        foreach (LogicalSlice slice in payloads)
        {
            if (options.Resume && HasTerminalProbeResult(connection, slice.PayloadId))
            {
                skipped++;
                continue;
            }

            attempted++;
            ProbeResult result = ProbeOnePayload(connection, slice, tempRoot);
            if (result.Status == "loaded") loaded++;
            else failed++;

            if (attempted % 100 == 0)
            {
                Console.Error.WriteLine($"[VFS] metadata shard {shard.Index}/{shard.Total}: attempted={attempted}, loaded={loaded}, failed={failed}, skipped={skipped}");
            }
        }

        SafeDeleteDirectory(tempRoot);
        Emit(new
        {
            status = "ok",
            command = "probe-vfs-metadata",
            db = dbPath,
            shard = shard.ToString(),
            attempted,
            skipped,
            loaded,
            failed,
        });
        return 0;
    }

    private static int ScanVfsTerms(CliOptions options)
    {
        string dbPath = RequirePath(options.VfsDbPath, "--vfs-db");
        string outputRoot = GetOutputRoot(options, dbPath);
        Directory.CreateDirectory(outputRoot);
        string[] terms = LoadTerms(options.ScanVfsTerms!);
        if (terms.Length == 0)
        {
            Emit(new { status = "error", error = "--scan-vfs-terms resolved zero terms." });
            return 1;
        }

        ShardSpec shard = ShardSpec.Parse(options.Shard);
        using SqliteConnection connection = OpenDb(dbPath);
        EnsureSchema(connection);

        List<LogicalSlice> payloads = LoadPreferredAbPayloads(connection);
        if (shard.Total > 1)
        {
            payloads = payloads.Where((_, index) => index % shard.Total == shard.Index).ToList();
        }

        List<object> hits = [];
        byte[][] termBytes = terms.Select(static t => Encoding.ASCII.GetBytes(t.ToLowerInvariant())).ToArray();
        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO term_hits (payload_id, logical_path, terms_json)
            VALUES ($payload_id, $logical_path, $terms_json);
            """;
        AddParams(insert, "$payload_id", "$logical_path", "$terms_json");

        int scanned = 0;
        foreach (LogicalSlice slice in payloads)
        {
            scanned++;
            if (!TryReadLogicalData(slice, out byte[] data, out string error))
            {
                Console.Error.WriteLine($"[VFS] term scan skip {slice.LogicalPath}: {error}");
                continue;
            }

            List<string> matched = [];
            for (int i = 0; i < terms.Length; i++)
            {
                if (ContainsAsciiIgnoreCase(data, termBytes[i]))
                {
                    matched.Add(terms[i]);
                }
            }

            if (matched.Count == 0)
            {
                continue;
            }

            string termsJson = JsonConvert.SerializeObject(matched);
            insert.Parameters["$payload_id"].Value = slice.PayloadId;
            insert.Parameters["$logical_path"].Value = slice.LogicalPath;
            insert.Parameters["$terms_json"].Value = termsJson;
            insert.ExecuteNonQuery();

            hits.Add(new
            {
                slice.LogicalId,
                slice.LogicalPath,
                slice.SourceRoot,
                slice.Length,
                slice.FileDataMd5,
                terms = matched,
            });
        }

        tx.Commit();

        string outPath = options.ClosureOut ?? Path.Combine(outputRoot, $"vfs_term_hits_{shard.Index}_{shard.Total}.json");
        WriteJson(outPath, hits);
        Emit(new
        {
            status = "ok",
            command = "scan-vfs-terms",
            db = dbPath,
            shard = shard.ToString(),
            scanned,
            hits = hits.Count,
            output = outPath,
        });
        return 0;
    }

    private static int WriteVfsClosure(CliOptions options, string seed)
    {
        string dbPath = RequirePath(options.VfsDbPath, "--vfs-db");
        using SqliteConnection connection = OpenDb(dbPath);
        EnsureSchema(connection);
        VfsClosure closure = ResolveClosure(connection, seed);

        string outputRoot = GetOutputRoot(options, dbPath);
        Directory.CreateDirectory(outputRoot);
        string outPath = options.ClosureOut ?? Path.Combine(outputRoot, "vfs_closure.json");
        WriteJson(outPath, closure);
        Emit(new
        {
            status = closure.UnresolvedCabs.Count == 0 ? "ok" : "partial",
            command = "vfs-deps",
            seed,
            payloads = closure.Payloads.Count,
            cabs = closure.Cabs.Count,
            unresolved_cabs = closure.UnresolvedCabs.Count,
            output = outPath,
        });
        return closure.Payloads.Count == 0 ? 4 : 0;
    }

    private static int LoadLogical(CliOptions options)
    {
        string dbPath = RequirePath(options.VfsDbPath, "--vfs-db");
        string outputRoot = GetOutputRoot(options, dbPath);
        string seed = options.LoadLogical!;
        string[] seeds = SplitSeeds(seed);

        using SqliteConnection connection = OpenDb(dbPath);
        EnsureSchema(connection);

        VfsClosure closure = ResolveSeedSet(connection, seeds, options.ResolveVfsDeps);

        if (closure.Payloads.Count == 0)
        {
            Emit(new { status = "error", command = "load-logical", error = $"No VFS payloads resolved for seed: {seed}" });
            return 4;
        }

        string materializedRoot = Path.Combine(outputRoot, "materialized", SafeFileName(string.Join("_", seeds)));
        RecreateDirectory(materializedRoot, outputRoot);
        List<object> materialized = [];
        foreach (ClosurePayload payload in closure.Payloads)
        {
            if (!TryReadLogicalData(payload.Slice, out byte[] data, out string error))
            {
                materialized.Add(new { payload.Slice.LogicalPath, status = "read_failed", error });
                continue;
            }

            string destination = Path.Combine(materializedRoot, SafeRelativePath(payload.Slice.LogicalPath));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, data);
            materialized.Add(new { payload.Slice.LogicalPath, status = "written", path = destination, payload.Slice.Length });
        }

        string manifestPath = Path.Combine(outputRoot, "vfs_load_manifest.json");
        WriteJson(manifestPath, new
        {
            seed,
            seeds,
            resolve_vfs_deps = options.ResolveVfsDeps,
            closure,
            materialized_root = materializedRoot,
            materialized,
        });

        CliOptions inner = new()
        {
            Hooks = options.Hooks,
            LoadPaths = [materializedRoot],
            ExportPath = options.ExportPath,
            Types = options.Types,
            Names = options.Names,
            SmokeTestLimit = options.SmokeTestLimit,
            Silent = options.Silent,
            LogLevel = options.LogLevel,
            FailFast = options.FailFast,
        };

        int result = HeadlessRunner.Run(inner);
        Console.Error.WriteLine($"[VFS] load manifest: {manifestPath}");
        return result;
    }

    private static int InspectUnityMaterials(CliOptions options)
    {
        string projectPath = RequireExistingDirectory(options.RepairUnityMaterials, "--repair-unity-materials");
        string reportRoot = options.RepairReport ?? Path.Combine(projectPath, "VfsMaterialReports");
        Directory.CreateDirectory(reportRoot);

        string assetsRoot = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            Emit(new { status = "error", error = $"Unity project Assets folder not found: {assetsRoot}" });
            return 1;
        }

        ShaderRepairSummary shaderRepair = RepairShaderLinksFromManifest(options, projectPath, assetsRoot, reportRoot);

        List<object> shaderAssets = FindUnityAssetGuids(assetsRoot, [".shader", ".shadergraph", ".asset"], static path =>
            path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
            || File.ReadLines(path).Take(8).Any(static line => line.Contains("Shader", StringComparison.OrdinalIgnoreCase)));

        List<object> textureAssets = FindUnityAssetGuids(assetsRoot, [".png", ".tga", ".jpg", ".jpeg", ".asset"], static path =>
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith("T_", StringComparison.OrdinalIgnoreCase));

        int shaderDeadbeef = 0;
        int textureDeadbeef = 0;
        List<object> unresolved = [];
        foreach (string materialPath in Directory.EnumerateFiles(assetsRoot, "*.mat", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(materialPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains(DeadbeefGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string kind = line.Contains("m_Shader:", StringComparison.Ordinal) ? "shader"
                    : line.Contains("m_Texture:", StringComparison.Ordinal) ? "texture"
                    : "unknown";
                if (kind == "shader") shaderDeadbeef++;
                if (kind == "texture") textureDeadbeef++;

                unresolved.Add(new
                {
                    material = Path.GetRelativePath(projectPath, materialPath),
                    line = i + 1,
                    kind,
                    text = line.Trim(),
                    reason = "No exact PPtr-to-exported-GUID mapping is available; not replacing with fallback.",
                });
            }
        }

        var report = new
        {
            generated_at = DateTimeOffset.Now,
            project_path = projectPath,
            shader_assets = shaderAssets,
            texture_assets = textureAssets,
            shader_deadbeef_refs = shaderDeadbeef,
            texture_deadbeef_refs = textureDeadbeef,
            unresolved,
            shader_repair = shaderRepair,
            fallback_used = false,
        };

        string jsonPath = Path.Combine(reportRoot, "zhuangfy_unresolved_dependencies.json");
        string mdPath = Path.Combine(reportRoot, "zhuangfy_unresolved_dependencies.md");
        WriteJson(jsonPath, report);
        WriteMaterialReportMarkdown(mdPath, reportRoot, shaderDeadbeef, textureDeadbeef, unresolved.Count, shaderAssets.Count, textureAssets.Count);

        Emit(new
        {
            status = shaderDeadbeef == 0 && textureDeadbeef == 0 ? "ok" : "partial",
            command = "repair-unity-materials",
            project_path = projectPath,
            shader_deadbeef_refs = shaderDeadbeef,
            texture_deadbeef_refs = textureDeadbeef,
            fallback_used = false,
            report = jsonPath,
        });
        return 0;
    }

    private static ShaderRepairSummary RepairShaderLinksFromManifest(CliOptions options, string projectPath, string assetsRoot, string reportRoot)
    {
        string outputRoot = options.OutputRoot ?? GuessOutputRootFromProject(projectPath);
        string manifestPath = Path.Combine(outputRoot, "vfs_load_manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new ShaderRepairSummary(
                "skipped",
                manifestPath,
                string.Empty,
                0,
                0,
                0,
                0,
                [],
                ["vfs_load_manifest.json not found; shader links were only inspected."]);
        }

        string materializedRoot;
        try
        {
            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            materializedRoot = manifest.Value<string>("materialized_root") ?? string.Empty;
        }
        catch (Exception ex)
        {
            return new ShaderRepairSummary("skipped", manifestPath, string.Empty, 0, 0, 0, 0, [], [$"Cannot parse manifest: {ex.Message}"]);
        }

        if (!Directory.Exists(materializedRoot))
        {
            return new ShaderRepairSummary("skipped", manifestPath, materializedRoot, 0, 0, 0, 0, [], ["Materialized closure directory not found."]);
        }

        string[] bundlePaths = Directory.GetFiles(materializedRoot, "*.ab", SearchOption.AllDirectories);
        if (bundlePaths.Length == 0)
        {
            return new ShaderRepairSummary("skipped", manifestPath, materializedRoot, 0, 0, 0, 0, [], ["Materialized closure contains no .ab files."]);
        }

        try
        {
            FullConfiguration settings = CreateSettings();
            ExportHandler handler = new(settings);
            GameData gameData = handler.Load(bundlePaths, LocalFileSystem.Instance);

            Dictionary<string, string> materialShaderKeys = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IShader> shadersByKey = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<AssetCollection>> collectionsByCab = gameData.GameBundle.FetchAssetCollections()
                .GroupBy(static collection => NormalizeCabName(collection.Name), StringComparer.OrdinalIgnoreCase)
                .Where(static group => IsVfsCabName(group.Key))
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<(string OwnerCab, int FileId), string> dependencyCabsByOwner = LoadDependencyCabLookup(options.VfsDbPath ?? Path.Combine(outputRoot, "endfield_vfs.sqlite"));
            List<MaterialShaderProbe> shaderProbes = [];

            foreach (IMaterial material in gameData.GameBundle.FetchAssets().OfType<IMaterial>())
            {
                if (TryResolveMaterialShader(material, collectionsByCab, dependencyCabsByOwner, out IShader? directShader))
                {
                    string shaderKey = MakeAssetKey(directShader);
                    materialShaderKeys[material.GetBestName()] = shaderKey;
                    shadersByKey.TryAdd(shaderKey, directShader);
                    continue;
                }

                if (shaderProbes.Count < 128)
                {
                    shaderProbes.Add(CreateMaterialShaderProbe(material, collectionsByCab, dependencyCabsByOwner));
                }

                foreach ((_, AssetRipper.Assets.Metadata.PPtr pptr) in material.FetchDependencies())
                {
                    if (pptr.IsNull)
                    {
                        continue;
                    }

                    if (material.Collection.TryGetAsset(pptr, out IUnityObjectBase? dependency) && dependency is IShader shader)
                    {
                        string shaderKey = MakeAssetKey(shader);
                        materialShaderKeys[material.GetBestName()] = shaderKey;
                        shadersByKey.TryAdd(shaderKey, shader);
                        break;
                    }
                }
            }

            if (shaderProbes.Count > 0)
            {
                WriteJson(Path.Combine(reportRoot, "zhuangfy_shader_probe.json"), shaderProbes);
            }

            Dictionary<string, ShaderExportRecord> exportedShaders = [];
            List<ShaderLinkRepair> repairs = [];
            List<string> errors = [];
            foreach ((string materialName, string shaderKey) in materialShaderKeys.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!shadersByKey.TryGetValue(shaderKey, out IShader? shader))
                {
                    continue;
                }

                if (!exportedShaders.TryGetValue(shaderKey, out ShaderExportRecord? exportRecord))
                {
                    exportRecord = ExportShaderAsset(projectPath, shader, reportRoot);
                    exportedShaders[shaderKey] = exportRecord;
                    if (!exportRecord.Success)
                    {
                        errors.Add($"{shader.GetBestName()}: {exportRecord.Error}");
                    }
                }

                int patched = 0;
                if (exportRecord.Success)
                {
                    foreach (string materialPath in FindMaterialFilesByName(assetsRoot, materialName))
                    {
                        if (PatchMaterialShaderGuid(materialPath, exportRecord.Guid, exportRecord.AssetType))
                        {
                            patched++;
                        }
                    }
                }

                repairs.Add(new ShaderLinkRepair(
                    materialName,
                    shader.GetBestName(),
                    shader.Collection.Name,
                    shader.PathID,
                    exportRecord.Guid,
                    exportRecord.AssetType,
                    exportRecord.RelativePath,
                    exportRecord.Exporter,
                    exportRecord.DecompileFailed,
                    patched,
                    exportRecord.Error));
            }

            string status = errors.Count == 0 && repairs.Any(static repair => repair.PatchedMaterials > 0)
                ? "ok"
                : repairs.Count == 0 ? "skipped" : "partial";

            return new ShaderRepairSummary(
                status,
                manifestPath,
                materializedRoot,
                bundlePaths.Length,
                materialShaderKeys.Count,
                exportedShaders.Values.Count(static shader => shader.Success),
                repairs.Sum(static repair => repair.PatchedMaterials),
                repairs,
                errors);
        }
        catch (Exception ex)
        {
            return new ShaderRepairSummary("error", manifestPath, materializedRoot, bundlePaths.Length, 0, 0, 0, [], [$"{ex.GetType().Name}: {ex.Message}"]);
        }
    }

    private static MaterialShaderProbe CreateMaterialShaderProbe(
        IMaterial material,
        IReadOnlyDictionary<string, List<AssetCollection>> collectionsByCab,
        IReadOnlyDictionary<(string OwnerCab, int FileId), string> dependencyCabsByOwner)
    {
        bool converted = TryConvertPPtr(material.Shader_C21, out AssetRipper.Assets.Metadata.PPtr pptr);
        string ownerCab = NormalizeCabName(material.Collection.Name);
        string? dependencyCab = converted && dependencyCabsByOwner.TryGetValue((ownerCab, pptr.FileID), out string? foundCab)
            ? foundCab
            : null;
        int dependencyCollectionCount = dependencyCab is not null && collectionsByCab.TryGetValue(dependencyCab, out List<AssetCollection>? collections)
            ? collections.Count
            : 0;
        List<string> dependencyShaderSamples = [];
        List<string> dependencyAssetSamples = [];
        string? resolvedType = null;
        if (converted && !pptr.IsNull && material.Collection.TryGetAsset(pptr, out IUnityObjectBase? collectionDependency))
        {
            resolvedType = collectionDependency.GetType().FullName;
        }
        else if (converted && dependencyCab is not null && collectionsByCab.TryGetValue(dependencyCab, out List<AssetCollection>? dependencyCollections))
        {
            foreach (AssetCollection collection in dependencyCollections)
            {
                foreach (IUnityObjectBase asset in collection.Take(8))
                {
                    dependencyAssetSamples.Add($"{asset.PathID}:{asset.ClassID}:{asset.GetType().FullName}:{asset.GetBestName()}");
                }

                foreach (IShader shader in collection.OfType<IShader>().Take(8))
                {
                    dependencyShaderSamples.Add($"{shader.PathID}:{shader.GetBestName()}");
                }

                if (collection.TryGetAsset(pptr.PathID, out IUnityObjectBase? externalDependency))
                {
                    resolvedType = externalDependency.GetType().FullName;
                    break;
                }
            }
        }

        return new MaterialShaderProbe(
            material.GetBestName(),
            material.GetType().FullName ?? material.GetType().Name,
            material.Collection.Name,
            ownerCab,
            material.Shader_C21.GetType().FullName ?? material.Shader_C21.GetType().Name,
            converted,
            converted ? pptr.FileID : null,
            converted ? pptr.PathID : null,
            dependencyCab,
            dependencyCollectionCount,
            resolvedType,
            dependencyShaderSamples,
            dependencyAssetSamples);
    }

    private static ShaderExportRecord ExportShaderAsset(string projectPath, IShader shader, string reportRoot)
    {
        Directory.CreateDirectory(reportRoot);
        MinimalShaderExportContainer container = new(shader.Collection);
        SimpleShaderExporter simpleExporter = new();
        ShaderRuriDecompileExporter decompileExporter = new();

        try
        {
            ShaderExporterBase exporter = simpleExporter.TryCreateCollection(shader, out _)
                ? simpleExporter
                : decompileExporter;
            ShaderExportCollection collection = new(exporter, shader);
            if (collection.Export(container, projectPath, LocalFileSystem.Instance))
            {
                string guid = collection.GUID.ToString();
                return new ShaderExportRecord(
                    true,
                    guid,
                    (int)AssetType.Meta,
                    FindAssetRelativePathByGuid(projectPath, guid),
                    exporter.GetType().Name,
                    false,
                    string.Empty);
            }
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(reportRoot, "shader_decompile_failures.log");
            File.AppendAllText(logPath, $"{shader.Collection.Name}:{shader.PathID} {shader.GetBestName()} {ex}\n", Encoding.UTF8);
        }

        try
        {
            YamlShaderExporter yamlExporter = new();
            if (yamlExporter.TryCreateCollection(shader, out IExportCollection? yamlCollection)
                && yamlCollection is ExportCollection exportCollection
                && exportCollection.Export(container, projectPath, LocalFileSystem.Instance))
            {
                string guid = exportCollection.GUID.ToString();
                return new ShaderExportRecord(
                    true,
                    guid,
                    (int)AssetType.Serialized,
                    FindAssetRelativePathByGuid(projectPath, guid),
                    nameof(YamlShaderExporter),
                    true,
                    "Decompile export failed; exported serialized Shader asset instead.");
            }
        }
        catch (Exception ex)
        {
            return new ShaderExportRecord(false, string.Empty, 0, string.Empty, nameof(YamlShaderExporter), true, $"{ex.GetType().Name}: {ex.Message}");
        }

        return new ShaderExportRecord(false, string.Empty, 0, string.Empty, string.Empty, true, "No shader exporter produced a file.");
    }

    private static Dictionary<(string OwnerCab, int FileId), string> LoadDependencyCabLookup(string dbPath)
    {
        Dictionary<(string OwnerCab, int FileId), string> result = [];
        if (!File.Exists(dbPath))
        {
            return result;
        }

        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_cab, file_id, dependency_cab
            FROM dependency_edges
            WHERE owner_cab LIKE 'cab-%' AND dependency_cab LIKE 'cab-%'
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string ownerCab = NormalizeCabName(reader.GetString(0));
            int fileId = reader.GetInt32(1);
            string dependencyCab = NormalizeCabName(reader.GetString(2));
            if (IsVfsCabName(ownerCab) && IsVfsCabName(dependencyCab))
            {
                result.TryAdd((ownerCab, fileId), dependencyCab);
            }
        }

        return result;
    }

    private static bool TryResolveMaterialShader(
        IMaterial material,
        IReadOnlyDictionary<string, List<AssetCollection>> collectionsByCab,
        IReadOnlyDictionary<(string OwnerCab, int FileId), string> dependencyCabsByOwner,
        [NotNullWhen(true)] out IShader? shader)
    {
        shader = null;
        if (material.Shader_C21P is { } directShader)
        {
            shader = directShader;
            return true;
        }

        if (TryConvertPPtr(material.Shader_C21, out AssetRipper.Assets.Metadata.PPtr materialShaderPPtr)
            && !materialShaderPPtr.IsNull
            && material.Collection.TryGetAsset(materialShaderPPtr, out IUnityObjectBase? materialShaderDependency)
            && materialShaderDependency is IShader materialShader)
        {
            shader = materialShader;
            return true;
        }

        if (TryResolveExternalCabPPtr(material, materialShaderPPtr, collectionsByCab, dependencyCabsByOwner, out shader))
        {
            return true;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (PropertyInfo property in material.GetType().GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(material);
            }
            catch
            {
                continue;
            }

            if (TryConvertPPtr(value, out AssetRipper.Assets.Metadata.PPtr pptr)
                && !pptr.IsNull
                && material.Collection.TryGetAsset(pptr, out IUnityObjectBase? dependency)
                && dependency is IShader resolvedShader)
            {
                shader = resolvedShader;
                return true;
            }

            if (TryConvertPPtr(value, out pptr)
                && TryResolveExternalCabPPtr(material, pptr, collectionsByCab, dependencyCabsByOwner, out shader))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveExternalCabPPtr(
        IMaterial material,
        AssetRipper.Assets.Metadata.PPtr pptr,
        IReadOnlyDictionary<string, List<AssetCollection>> collectionsByCab,
        IReadOnlyDictionary<(string OwnerCab, int FileId), string> dependencyCabsByOwner,
        [NotNullWhen(true)] out IShader? shader)
    {
        shader = null;
        if (pptr.IsNull || pptr.FileID <= 0)
        {
            return false;
        }

        string ownerCab = NormalizeCabName(material.Collection.Name);
        if (!IsVfsCabName(ownerCab)
            || !dependencyCabsByOwner.TryGetValue((ownerCab, pptr.FileID), out string? dependencyCab)
            || !collectionsByCab.TryGetValue(dependencyCab, out List<AssetCollection>? collections))
        {
            return false;
        }

        foreach (AssetCollection collection in collections)
        {
            if (collection.TryGetAsset(pptr.PathID, out IUnityObjectBase? dependency)
                && dependency is IShader resolvedShader)
            {
                shader = resolvedShader;
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertPPtr(object? value, out AssetRipper.Assets.Metadata.PPtr pptr)
    {
        pptr = default;
        if (value is AssetRipper.Assets.Metadata.PPtr direct)
        {
            pptr = direct;
            return true;
        }

        if (value is AssetRipper.Assets.Metadata.IPPtr interfacePPtr)
        {
            pptr = new AssetRipper.Assets.Metadata.PPtr(interfacePPtr.FileID, interfacePPtr.PathID);
            return true;
        }

        if (value is null)
        {
            return false;
        }

        Type type = value.GetType();
        if (!type.Name.StartsWith("PPtr", StringComparison.Ordinal))
        {
            return false;
        }

        PropertyInfo? fileIdProperty = type.GetProperty("FileID", BindingFlags.Instance | BindingFlags.Public);
        PropertyInfo? pathIdProperty = type.GetProperty("PathID", BindingFlags.Instance | BindingFlags.Public);
        if (fileIdProperty is null || pathIdProperty is null)
        {
            return false;
        }

        object? fileIdValue = fileIdProperty.GetValue(value);
        object? pathIdValue = pathIdProperty.GetValue(value);
        if (fileIdValue is null || pathIdValue is null)
        {
            return false;
        }

        pptr = new AssetRipper.Assets.Metadata.PPtr(Convert.ToInt32(fileIdValue), Convert.ToInt64(pathIdValue));
        return true;
    }

    private static IEnumerable<string> FindMaterialFilesByName(string assetsRoot, string materialName)
    {
        string expected = $"{FileSystem.FixInvalidFileNameCharacters(materialName)}.mat";
        return Directory.EnumerateFiles(assetsRoot, "*.mat", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PatchMaterialShaderGuid(string materialPath, string guid, int assetType)
    {
        string[] lines = File.ReadAllLines(materialPath);
        bool patched = false;
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = ShaderLinePattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            lines[i] = $"{match.Groups["prefix"].Value}{{fileID: 4800000, guid: {guid}, type: {assetType}}}";
            patched = true;
            break;
        }

        if (patched)
        {
            File.WriteAllLines(materialPath, lines, new UTF8Encoding(false));
        }

        return patched;
    }

    private static string FindAssetRelativePathByGuid(string projectPath, string guid)
    {
        foreach (string metaPath in Directory.EnumerateFiles(projectPath, "*.meta", SearchOption.AllDirectories))
        {
            if (File.ReadLines(metaPath).Any(line => line.Trim().Equals($"guid: {guid}", StringComparison.OrdinalIgnoreCase)))
            {
                string assetPath = metaPath[..^".meta".Length];
                return NormalizeLogicalPath(Path.GetRelativePath(projectPath, assetPath));
            }
        }

        return string.Empty;
    }

    private static string GuessOutputRootFromProject(string projectPath)
    {
        DirectoryInfo? directory = new(projectPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vfs_load_manifest.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return Path.GetDirectoryName(projectPath) ?? projectPath;
    }

    private static string MakeAssetKey(IUnityObjectBase asset) => $"{asset.Collection.Name}:{asset.PathID}";

    private static ProbeResult ProbeOnePayload(SqliteConnection connection, LogicalSlice slice, string tempRoot)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string tempPath = Path.Combine(tempRoot, $"{slice.LogicalId}_{Path.GetFileName(slice.LogicalPath)}");
        try
        {
            if (!TryReadLogicalData(slice, out byte[] data, out string error))
            {
                ProbeResult readFailed = new("read_failed", 0, 0, 0, 0, sw.ElapsedMilliseconds, error);
                SaveProbeResult(connection, slice, readFailed, [], []);
                return readFailed;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            File.WriteAllBytes(tempPath, data);

            FileBase file = SchemeReader.ReadFile(data, tempPath, Path.GetFileName(tempPath));
            if (file is FileContainer container)
            {
                container.ReadContentsRecursively();
            }

            SerializedFile[] serializedFiles = file switch
            {
                SerializedFile serializedFile => [serializedFile],
                FileContainer fileContainer => fileContainer.FetchSerializedFiles().ToArray(),
                _ => [],
            };

            int assetCount = serializedFiles.Sum(static serializedFile => serializedFile.Objects.Length);
            int failedFiles = file is FileContainer failedContainer ? CountFailedFiles(failedContainer) : 0;
            int collectionCount = serializedFiles.Length;
            int bundleCount = file is FileContainer bundleContainer ? CountContainers(bundleContainer) : 0;
            var classCounts = serializedFiles
                .SelectMany(static serializedFile => serializedFile.Objects.ToArray())
                .GroupBy(static info => info.ClassID)
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Key)
                .Take(80)
                .Select(static group => new CountPair(GetClassLabel(group.Key), group.Count()))
                .ToArray();

            List<CollectionMetadata> collections = [];
            for (int serializedFileIndex = 0; serializedFileIndex < serializedFiles.Length; serializedFileIndex++)
            {
                SerializedFile serializedFile = serializedFiles[serializedFileIndex];
                string cabName = NormalizeCabName(serializedFile.NameFixed);
                if (!IsVfsCabName(cabName))
                {
                    continue;
                }

                DependencyMetadata[] dependencies = serializedFile.Dependencies
                    .ToArray()
                    .Select((dependency, index) => new DependencyMetadata(
                        index + 1,
                        NormalizeCabNameFromDependency(dependency.ToString() ?? string.Empty),
                        dependency.ToString() ?? string.Empty))
                    .Where(static dependency => IsVfsCabName(dependency.Name))
                    .Where(dependency => !string.Equals(dependency.Name, cabName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(static dependency => dependency.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray();

                collections.Add(new CollectionMetadata(
                    cabName,
                    MakeCollectionPath(serializedFile, serializedFileIndex),
                    serializedFile.Objects.Length,
                    dependencies));
            }

            ProbeResult result = new("loaded", assetCount, failedFiles, collectionCount, bundleCount, sw.ElapsedMilliseconds, string.Empty);
            SaveProbeResult(connection, slice, result, collections, classCounts);
            return result;
        }
        catch (Exception ex)
        {
            ProbeResult result = new("exception", 0, 0, 0, 0, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
            SaveProbeResult(connection, slice, result, [], []);
            return result;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static VfsClosure ResolveSingleSeed(SqliteConnection connection, string seed)
    {
        LogicalSlice? slice = FindLogicalSlice(connection, seed);
        if (slice is null)
        {
            return new VfsClosure(seed, [], [], [seed]);
        }

        return new VfsClosure(seed, [new ClosurePayload(slice.PayloadId, slice, [])], [], []);
    }

    private static VfsClosure ResolveSeedSet(SqliteConnection connection, IReadOnlyList<string> seeds, bool resolveDeps)
    {
        Dictionary<string, ClosurePayload> payloads = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cabs = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);

        foreach (string seed in seeds)
        {
            VfsClosure next = resolveDeps ? ResolveClosure(connection, seed) : ResolveSingleSeed(connection, seed);
            foreach (ClosurePayload payload in next.Payloads)
            {
                payloads.TryAdd(payload.PayloadId, payload);
            }
            foreach (string cab in next.Cabs)
            {
                cabs.Add(cab);
            }
            foreach (string cab in next.UnresolvedCabs)
            {
                unresolved.Add(cab);
            }
        }

        return new VfsClosure(
            string.Join(";", seeds),
            payloads.Values.OrderBy(static p => p.Slice.LogicalPath, StringComparer.OrdinalIgnoreCase).ToList(),
            cabs.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            unresolved.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static VfsClosure ResolveClosure(SqliteConnection connection, string seed)
    {
        Dictionary<string, ClosurePayload> payloads = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenCabs = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> cabQueue = new();
        List<string> unresolvedCabs = [];

        LogicalSlice? seedSlice = FindLogicalSlice(connection, seed);
        if (seedSlice is not null)
        {
            foreach (string cab in LoadCabsForPayload(connection, seedSlice.PayloadId))
            {
                cabQueue.Enqueue(cab);
            }

            payloads.TryAdd(seedSlice.PayloadId, new ClosurePayload(seedSlice.PayloadId, seedSlice, []));
        }
        else
        {
            cabQueue.Enqueue(seed);
        }

        while (cabQueue.Count > 0)
        {
            string cab = cabQueue.Dequeue();
            if (!seenCabs.Add(cab))
            {
                continue;
            }

            List<string> ownerPayloads = LoadPayloadsForCab(connection, cab);
            if (ownerPayloads.Count == 0)
            {
                unresolvedCabs.Add(cab);
                continue;
            }

            foreach (string payloadId in ownerPayloads.Take(1))
            {
                LogicalSlice? slice = FindPreferredSliceForPayload(connection, payloadId);
                if (slice is not null && !payloads.ContainsKey(payloadId))
                {
                    payloads[payloadId] = new ClosurePayload(payloadId, slice, LoadCabsForPayload(connection, payloadId));
                }

                foreach (string dependency in LoadDependenciesForCab(connection, payloadId, cab))
                {
                    cabQueue.Enqueue(dependency);
                }
            }
        }

        return new VfsClosure(
            seed,
            payloads.Values.OrderBy(static p => p.Slice.LogicalPath, StringComparer.OrdinalIgnoreCase).ToList(),
            seenCabs.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            unresolvedCabs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void SaveProbeResult(
        SqliteConnection connection,
        LogicalSlice slice,
        ProbeResult result,
        IReadOnlyList<CollectionMetadata> collections,
        IReadOnlyList<CountPair> classCounts)
    {
        using SqliteTransaction tx = connection.BeginTransaction();
        Execute(connection, tx, "DELETE FROM probe_results WHERE payload_id = $payload_id;", ("$payload_id", slice.PayloadId));
        Execute(connection, tx, "DELETE FROM bundle_metadata WHERE payload_id = $payload_id;", ("$payload_id", slice.PayloadId));
        Execute(connection, tx, "DELETE FROM dependency_edges WHERE owner_payload_id = $payload_id;", ("$payload_id", slice.PayloadId));

        Execute(connection, tx, """
            INSERT INTO probe_results
            (payload_id, status, loaded_assets, failed_files, collection_count, bundle_count, elapsed_ms, error, probed_at)
            VALUES ($payload_id, $status, $loaded_assets, $failed_files, $collection_count, $bundle_count, $elapsed_ms, $error, $probed_at);
            """,
            ("$payload_id", slice.PayloadId),
            ("$status", result.Status),
            ("$loaded_assets", result.LoadedAssets),
            ("$failed_files", result.FailedFiles),
            ("$collection_count", result.CollectionCount),
            ("$bundle_count", result.BundleCount),
            ("$elapsed_ms", result.ElapsedMs),
            ("$error", result.Error),
            ("$probed_at", DateTimeOffset.Now.ToString("O")));

        string classCountsJson = JsonConvert.SerializeObject(classCounts);
        foreach (CollectionMetadata collection in collections)
        {
            if (!IsVfsCabName(collection.CabName))
            {
                continue;
            }

            Execute(connection, tx, """
                INSERT INTO bundle_metadata
                (payload_id, logical_path, cab_name, collection_path, asset_count, class_counts_json)
                VALUES ($payload_id, $logical_path, $cab_name, $collection_path, $asset_count, $class_counts_json);
                """,
                ("$payload_id", slice.PayloadId),
                ("$logical_path", slice.LogicalPath),
                ("$cab_name", NormalizeCabName(collection.CabName)),
                ("$collection_path", collection.CollectionPath),
                ("$asset_count", collection.AssetCount),
                ("$class_counts_json", classCountsJson));

            foreach (DependencyMetadata dependency in collection.Dependencies)
            {
                string dependencyCab = NormalizeCabName(dependency.Name);
                if (!IsVfsCabName(dependencyCab)
                    || string.Equals(dependencyCab, collection.CabName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Execute(connection, tx, """
                    INSERT OR IGNORE INTO dependency_edges
                    (owner_payload_id, owner_logical_path, owner_cab, file_id, dependency_cab, dependency_path)
                    VALUES ($owner_payload_id, $owner_logical_path, $owner_cab, $file_id, $dependency_cab, $dependency_path);
                    """,
                    ("$owner_payload_id", slice.PayloadId),
                    ("$owner_logical_path", slice.LogicalPath),
                    ("$owner_cab", NormalizeCabName(collection.CabName)),
                    ("$file_id", dependency.FileId),
                    ("$dependency_cab", dependencyCab),
                    ("$dependency_path", dependency.Path));
            }
        }

        tx.Commit();
    }

    private static bool HasTerminalProbeResult(SqliteConnection connection, string payloadId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM probe_results WHERE payload_id = $payload_id LIMIT 1;";
        command.Parameters.AddWithValue("$payload_id", payloadId);
        object? value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return false;
        }

        string status = Convert.ToString(value) ?? string.Empty;
        return status is "loaded" or "exception" or "read_failed";
    }

    private static List<LogicalSlice> LoadPreferredAbPayloads(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT lf.logical_id, lf.sequence, lf.logical_path, lf.source_root, lf.block_info_path, lf.chunk_md5_name,
                   lf.chunk_path, lf.offset, lf.length, lf.file_data_md5, lf.extension, lf.use_encrypt, lf.payload_id
            FROM payloads p
            JOIN logical_files lf ON lf.logical_id = p.preferred_logical_id
            WHERE lf.extension = '.ab'
            ORDER BY lf.logical_path COLLATE NOCASE, lf.logical_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<LogicalSlice> result = [];
        while (reader.Read())
        {
            result.Add(ReadLogicalSlice(reader));
        }
        return result;
    }

    private static List<LogicalSlice> LoadTermHitAbPayloads(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT lf.logical_id, lf.sequence, lf.logical_path, lf.source_root, lf.block_info_path, lf.chunk_md5_name,
                   lf.chunk_path, lf.offset, lf.length, lf.file_data_md5, lf.extension, lf.use_encrypt, lf.payload_id
            FROM term_hits th
            JOIN payloads p ON p.payload_id = th.payload_id
            JOIN logical_files lf ON lf.logical_id = p.preferred_logical_id
            WHERE lf.extension = '.ab'
            ORDER BY lf.logical_path COLLATE NOCASE, lf.logical_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<LogicalSlice> result = [];
        while (reader.Read())
        {
            result.Add(ReadLogicalSlice(reader));
        }
        return result;
    }

    private static LogicalSlice? FindLogicalSlice(SqliteConnection connection, string seed)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT logical_id, sequence, logical_path, source_root, block_info_path, chunk_md5_name,
                   chunk_path, offset, length, file_data_md5, extension, use_encrypt, payload_id
            FROM logical_files
            WHERE logical_path = $seed COLLATE NOCASE
               OR logical_path LIKE '%' || $seed COLLATE NOCASE
            ORDER BY chunk_exists DESC,
                     CASE source_root WHEN 'Persistent' THEN 0 WHEN 'StreamingAssets' THEN 1 ELSE 2 END,
                     length DESC, logical_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$seed", NormalizeLogicalPath(seed));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadLogicalSlice(reader) : null;
    }

    private static LogicalSlice? FindPreferredSliceForPayload(SqliteConnection connection, string payloadId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT lf.logical_id, lf.sequence, lf.logical_path, lf.source_root, lf.block_info_path, lf.chunk_md5_name,
                   lf.chunk_path, lf.offset, lf.length, lf.file_data_md5, lf.extension, lf.use_encrypt, lf.payload_id
            FROM payloads p
            JOIN logical_files lf ON lf.logical_id = p.preferred_logical_id
            WHERE p.payload_id = $payload_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$payload_id", payloadId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadLogicalSlice(reader) : null;
    }

    private static List<string> LoadCabsForPayload(SqliteConnection connection, string payloadId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT cab_name
            FROM bundle_metadata
            WHERE payload_id = $payload_id
              AND cab_name LIKE 'cab-%' COLLATE NOCASE
            ORDER BY cab_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$payload_id", payloadId);
        return ReadStringColumn(command);
    }

    private static List<string> LoadPayloadsForCab(SqliteConnection connection, string cab)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bm.payload_id
            FROM bundle_metadata bm
            JOIN payloads p ON p.payload_id = bm.payload_id
            JOIN logical_files lf ON lf.logical_id = p.preferred_logical_id
            WHERE bm.cab_name = $cab COLLATE NOCASE
              AND bm.cab_name LIKE 'cab-%' COLLATE NOCASE
            GROUP BY bm.payload_id
            ORDER BY MAX(lf.chunk_exists) DESC,
                     MIN(CASE lf.source_root WHEN 'Persistent' THEN 0 WHEN 'StreamingAssets' THEN 1 ELSE 2 END),
                     MAX(lf.length) DESC,
                     MIN(lf.logical_id);
            """;
        command.Parameters.AddWithValue("$cab", NormalizeCabName(cab));
        return ReadStringColumn(command);
    }

    private static List<string> LoadDependenciesForCab(SqliteConnection connection, string payloadId, string cab)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT dependency_cab
            FROM dependency_edges
            WHERE owner_payload_id = $payload_id
              AND owner_cab = $cab COLLATE NOCASE
              AND dependency_cab <> ''
              AND dependency_cab LIKE 'cab-%' COLLATE NOCASE
              AND dependency_cab <> owner_cab COLLATE NOCASE
            ORDER BY dependency_cab COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$payload_id", payloadId);
        command.Parameters.AddWithValue("$cab", NormalizeCabName(cab));
        return ReadStringColumn(command);
    }

    private static List<string> ReadStringColumn(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> result = [];
        while (reader.Read())
        {
            string? value = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }
        return result;
    }

    private static LogicalSlice ReadLogicalSlice(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11) != 0,
            reader.GetString(12));

    private static bool TryReadLogicalData(LogicalSlice slice, out byte[] data, out string error)
    {
        data = [];
        error = string.Empty;
        if (slice.Length > int.MaxValue)
        {
            error = $"slice too large to buffer: {slice.Length}";
            return false;
        }

        if (!slice.UseEncrypt)
        {
            if (!File.Exists(slice.ChunkPath))
            {
                error = $"chunk not found: {slice.ChunkPath}";
                return false;
            }

            using FileStream stream = File.OpenRead(slice.ChunkPath);
            long end = slice.Offset + slice.Length;
            if (slice.Offset < 0 || slice.Length < 0 || end < slice.Offset || end > stream.Length)
            {
                error = $"slice outside chunk bounds: {slice.Offset}+{slice.Length}>{stream.Length}";
                return false;
            }

            data = new byte[(int)slice.Length];
            stream.Position = slice.Offset;
            stream.ReadExactly(data);
            return true;
        }

        if (!EndField_0_8_25_Vfs.TryParseBlockInfo(slice.BlockInfoPath, out EndFieldVfsBlock block, out error))
        {
            return false;
        }

        foreach (EndFieldVfsChunk chunk in block.Chunks)
        {
            if (!chunk.Md5Name.Equals(slice.ChunkMd5Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (EndFieldVfsFile file in chunk.Files)
            {
                if (NormalizeLogicalPath(file.FileName).Equals(slice.LogicalPath, StringComparison.OrdinalIgnoreCase)
                    && checked((long)file.Offset) == slice.Offset
                    && checked((long)file.Length) == slice.Length)
                {
                    return EndField_0_8_25_Vfs.TryReadFile(block, chunk, file, out data, out error);
                }
            }
        }

        error = $"logical file not found in block info: {slice.LogicalPath}";
        return false;
    }

    private static SqliteConnection OpenDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? AppContext.BaseDirectory);
        SqliteConnection connection = new($"Data Source={dbPath};Cache=Shared");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA busy_timeout=60000;");
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS logical_files (
                logical_id INTEGER PRIMARY KEY AUTOINCREMENT,
                sequence INTEGER NOT NULL,
                logical_path TEXT NOT NULL,
                source_root TEXT NOT NULL,
                block_info_path TEXT NOT NULL,
                group_name TEXT NOT NULL,
                chunk_md5_name TEXT NOT NULL,
                chunk_path TEXT NOT NULL,
                chunk_exists INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                file_data_md5 TEXT NOT NULL,
                extension TEXT NOT NULL,
                block_type INTEGER NOT NULL,
                use_encrypt INTEGER NOT NULL,
                payload_id TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_logical_path ON logical_files(logical_path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_logical_payload ON logical_files(payload_id);
            CREATE INDEX IF NOT EXISTS ix_logical_extension ON logical_files(extension);

            CREATE TABLE IF NOT EXISTS payloads (
                payload_id TEXT PRIMARY KEY,
                length INTEGER NOT NULL,
                file_data_md5 TEXT NOT NULL,
                preferred_logical_id INTEGER NULL,
                alias_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS block_parse_errors (
                block_info_path TEXT NOT NULL,
                error TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS probe_results (
                payload_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                loaded_assets INTEGER NOT NULL,
                failed_files INTEGER NOT NULL,
                collection_count INTEGER NOT NULL,
                bundle_count INTEGER NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                error TEXT NOT NULL,
                probed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bundle_metadata (
                payload_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                cab_name TEXT NOT NULL,
                collection_path TEXT NOT NULL,
                asset_count INTEGER NOT NULL,
                class_counts_json TEXT NOT NULL,
                PRIMARY KEY(payload_id, cab_name, collection_path)
            );
            CREATE INDEX IF NOT EXISTS ix_bundle_cab ON bundle_metadata(cab_name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS dependency_edges (
                owner_payload_id TEXT NOT NULL,
                owner_logical_path TEXT NOT NULL,
                owner_cab TEXT NOT NULL,
                file_id INTEGER NOT NULL,
                dependency_cab TEXT NOT NULL,
                dependency_path TEXT NOT NULL,
                PRIMARY KEY(owner_payload_id, owner_cab, file_id, dependency_cab)
            );
            CREATE INDEX IF NOT EXISTS ix_dependency_owner ON dependency_edges(owner_cab COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_dependency_dep ON dependency_edges(dependency_cab COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS term_hits (
                payload_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                terms_json TEXT NOT NULL
            );
            """);
    }

    private static FullConfiguration CreateSettings()
    {
        GameFileLoader.Headless = true;
        FullConfiguration settings = new();
        settings.ImportSettings.StreamingAssetsMode = StreamingAssetsMode.Extract;
        settings.ProcessingSettings.BundledAssetsExportMode = BundledAssetsExportMode.DirectExport;
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
        return settings;
    }

    private static int CountFailedFiles(Bundle bundle) => bundle.FailedFiles.Count + bundle.Bundles.Sum(CountFailedFiles);

    private static int CountBundles(Bundle bundle) => 1 + bundle.Bundles.Sum(CountBundles);

    private static int CountFailedFiles(FileContainer container)
        => container.FailedFiles.Count + container.FileLists.Sum(CountFailedFiles);

    private static int CountContainers(FileContainer container)
        => 1 + container.FileLists.Sum(CountContainers);

    private static string GetClassLabel(short classId)
        => Enum.IsDefined(typeof(ClassIDType), (int)classId)
            ? ((ClassIDType)(int)classId).ToString()
            : classId.ToString();

    private static string DetectSourceRoot(string path)
    {
        string normalized = path.Replace('/', '\\');
        if (normalized.Contains("\\Persistent\\", StringComparison.OrdinalIgnoreCase)) return "Persistent";
        if (normalized.Contains("\\StreamingAssets\\", StringComparison.OrdinalIgnoreCase)) return "StreamingAssets";
        return "Unknown";
    }

    private static string NormalizeLogicalPath(string path) => path.Replace('\\', '/');

    private static string MakeCollectionPath(SerializedFile serializedFile, int index)
    {
        string path = string.IsNullOrWhiteSpace(serializedFile.FilePath)
            ? serializedFile.NameFixed
            : serializedFile.FilePath;
        return $"{path}#{index}";
    }

    private static string NormalizeCabName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = NormalizeCabNameFromDependency(value);
        return normalized.Length == 0 ? value.Trim().ToLowerInvariant() : normalized;
    }

    private static string NormalizeCabNameFromDependency(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Match match = CabNamePattern.Match(value);
        if (!match.Success)
        {
            return string.Empty;
        }

        string hash = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return $"cab-{hash.ToLowerInvariant()}";
    }

    private static bool IsVfsCabName(string value)
        => value.StartsWith("cab-", StringComparison.OrdinalIgnoreCase)
           && value.Length == 36;

    private static string MakePayloadId(ulong length, string md5) => $"{length:X16}:{md5.ToUpperInvariant()}";

    private static string SafeRelativePath(string path)
    {
        string normalized = NormalizeLogicalPath(path).TrimStart('/');
        foreach (char c in Path.GetInvalidPathChars())
        {
            normalized = normalized.Replace(c, '_');
        }
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SafeFileName(string value)
    {
        string result = value;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(c, '_');
        }
        result = result.Replace('/', '_').Replace('\\', '_');
        return result.Length > 120 ? result[^120..] : result;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> lowerNeedle)
    {
        if (lowerNeedle.Length == 0 || data.Length < lowerNeedle.Length)
        {
            return false;
        }

        for (int i = 0; i <= data.Length - lowerNeedle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < lowerNeedle.Length; j++)
            {
                byte b = data[i + j];
                if (b is >= (byte)'A' and <= (byte)'Z')
                {
                    b = (byte)(b + 32);
                }
                if (b != lowerNeedle[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return true;
        }
        return false;
    }

    private static string[] LoadTerms(string value)
    {
        if (File.Exists(value))
        {
            return File.ReadLines(value)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return value.Split([',', ';', '|', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SplitSeeds(string value)
        => value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void WriteIndexReports(
        string outputRoot,
        string gameRoot,
        int logicalCount,
        int abCount,
        long uniqueAbCount,
        long duplicateAbCount,
        int parseErrors,
        Dictionary<string, int> sourceCounts,
        Dictionary<string, int> extensionCounts,
        SqliteConnection connection)
    {
        string summary = Path.Combine(outputRoot, "vfs_scan_summary.md");
        StringBuilder builder = new();
        builder.AppendLine("# Endfield VFS scan summary");
        builder.AppendLine();
        builder.AppendLine($"- Game root: `{gameRoot}`");
        builder.AppendLine($"- Logical files: `{logicalCount}`");
        builder.AppendLine($"- Logical `.ab` files: `{abCount}`");
        builder.AppendLine($"- Unique logical `.ab` files: `{uniqueAbCount}`");
        builder.AppendLine($"- Duplicate logical `.ab` files: `{duplicateAbCount}`");
        builder.AppendLine($"- Parse errors: `{parseErrors}`");
        builder.AppendLine();
        builder.AppendLine("## Source roots");
        foreach ((string key, int count) in sourceCounts.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- `{key}`: `{count}`");
        }
        builder.AppendLine();
        builder.AppendLine("## Extension counts");
        foreach ((string key, int count) in extensionCounts.OrderByDescending(static p => p.Value).Take(40))
        {
            builder.AppendLine($"- `{key}`: `{count}`");
        }
        File.WriteAllText(summary, builder.ToString(), Encoding.UTF8);

        List<object> errors = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT block_info_path, error FROM block_parse_errors ORDER BY block_info_path COLLATE NOCASE;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            errors.Add(new { block_info_path = reader.GetString(0), error = reader.GetString(1) });
        }
        WriteJson(Path.Combine(outputRoot, "block_parse_errors.json"), errors);
    }

    private static List<object> FindUnityAssetGuids(string assetsRoot, string[] extensions, Func<string, bool> include)
    {
        List<object> result = [];
        foreach (string path in Directory.EnumerateFiles(assetsRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !include(path))
            {
                continue;
            }

            string? guid = ReadUnityMetaGuid(path);
            if (guid is null)
            {
                continue;
            }

            result.Add(new
            {
                path = Path.GetRelativePath(assetsRoot, path).Replace('\\', '/'),
                guid,
                name = Path.GetFileNameWithoutExtension(path),
            });
        }
        return result;
    }

    private static string? ReadUnityMetaGuid(string assetPath)
    {
        string meta = $"{assetPath}.meta";
        if (!File.Exists(meta))
        {
            return null;
        }

        foreach (string line in File.ReadLines(meta))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["guid:".Length..].Trim();
            }
        }
        return null;
    }

    private static void WriteMaterialReportMarkdown(string path, string reportRoot, int shaderDeadbeef, int textureDeadbeef, int unresolved, int shaderAssets, int textureAssets)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Zhuangfy material dependency report");
        builder.AppendLine();
        builder.AppendLine("- Fallback shader used: `false`");
        builder.AppendLine($"- Exported shader candidates: `{shaderAssets}`");
        builder.AppendLine($"- Exported texture candidates: `{textureAssets}`");
        builder.AppendLine($"- Deadbeef shader refs: `{shaderDeadbeef}`");
        builder.AppendLine($"- Deadbeef texture refs: `{textureDeadbeef}`");
        builder.AppendLine($"- Unresolved refs: `{unresolved}`");
        builder.AppendLine();
        builder.AppendLine("This pass does not replace unresolved shader refs with fallback. Any remaining deadbeef reference is a real dependency gap.");
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string RequirePath(string? path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{optionName} is required.");
        }
        return Path.GetFullPath(path);
    }

    private static string RequireExistingDirectory(string? path, string optionName)
    {
        string full = RequirePath(path, optionName);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"{optionName} not found: {full}");
        }
        return full;
    }

    private static string GetOutputRoot(CliOptions options, string dbPath)
        => Path.GetFullPath(options.OutputRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? AppContext.BaseDirectory, "vfs_reports"));

    private static void ConfigureLogging(CliOptions options)
    {
        Logger.Clear();
        if (!options.Silent)
        {
            Logger.Add(new StderrLogger { MinLevel = options.LogLevel });
        }
        Logger.Add(new FileLogger($"Ruri_Cli_{DateTime.Now:yyyyMMdd_HHmmss}.log"));
        Logger.AllowVerbose = options.LogLevel == LogType.Verbose || options.LogLevel == LogType.Debug;
    }

    private static void RecreateDirectory(string target, string allowedRoot)
    {
        string fullTarget = Path.GetFullPath(target);
        string fullRoot = Path.GetFullPath(allowedRoot);
        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to recreate outside output root: {fullTarget}");
        }
        if (Directory.Exists(fullTarget))
        {
            Directory.Delete(fullTarget, true);
        }
        Directory.CreateDirectory(fullTarget);
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private static void AddParams(SqliteCommand command, params string[] names)
    {
        foreach (string name in names)
        {
            SqliteParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
        }
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented), Encoding.UTF8);
    }

    private static void Emit(object value) => HeadlessRunner.JsonStdout.WriteLine(JsonConvert.SerializeObject(value));

    private sealed record ShardSpec(int Index, int Total)
    {
        public static ShardSpec Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new ShardSpec(0, 1);
            }

            string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int index) || !int.TryParse(parts[1], out int total) || total <= 0 || index < 0 || index >= total)
            {
                throw new ArgumentException($"Invalid --shard value '{value}'. Expected i/n, for example 0/4.");
            }
            return new ShardSpec(index, total);
        }

        public override string ToString() => $"{Index}/{Total}";
    }

    private sealed record LogicalSlice(
        long LogicalId,
        int Sequence,
        string LogicalPath,
        string SourceRoot,
        string BlockInfoPath,
        string ChunkMd5Name,
        string ChunkPath,
        long Offset,
        long Length,
        string FileDataMd5,
        string Extension,
        bool UseEncrypt,
        string PayloadId);

    private sealed record ProbeResult(string Status, int LoadedAssets, int FailedFiles, int CollectionCount, int BundleCount, long ElapsedMs, string Error);

    private sealed record CountPair(string Name, int Count);

    private sealed record DependencyMetadata(int FileId, string Name, string Path);

    private sealed record CollectionMetadata(string CabName, string CollectionPath, int AssetCount, IReadOnlyList<DependencyMetadata> Dependencies);

    private sealed record ClosurePayload(string PayloadId, LogicalSlice Slice, IReadOnlyList<string> Cabs);

    private sealed record VfsClosure(string Seed, IReadOnlyList<ClosurePayload> Payloads, IReadOnlyList<string> Cabs, IReadOnlyList<string> UnresolvedCabs);

    private sealed record ShaderExportRecord(
        bool Success,
        string Guid,
        int AssetType,
        string RelativePath,
        string Exporter,
        bool DecompileFailed,
        string Error);

    private sealed record ShaderLinkRepair(
        string Material,
        string Shader,
        string ShaderCab,
        long ShaderPathId,
        string ShaderGuid,
        int ShaderAssetType,
        string ShaderPath,
        string Exporter,
        bool DecompileFailed,
        int PatchedMaterials,
        string Error);

    private sealed record MaterialShaderProbe(
        string Material,
        string MaterialType,
        string CollectionName,
        string OwnerCab,
        string PPtrType,
        bool PPtrConverted,
        int? FileId,
        long? PathId,
        string? DependencyCab,
        int DependencyCollectionCount,
        string? ResolvedType,
        IReadOnlyList<string> DependencyShaderSamples,
        IReadOnlyList<string> DependencyAssetSamples);

    private sealed record ShaderRepairSummary(
        string Status,
        string ManifestPath,
        string MaterializedRoot,
        int MaterializedBundles,
        int MaterialsWithResolvedShader,
        int ExportedShaders,
        int PatchedMaterials,
        IReadOnlyList<ShaderLinkRepair> Repairs,
        IReadOnlyList<string> Errors);

    private sealed class MinimalShaderExportContainer(AssetCollection file) : IExportContainer
    {
        public long GetExportID(IUnityObjectBase asset) => ExportIdHandler.GetMainExportID(asset);

        public AssetType ToExportType(Type type) => AssetType.Meta;

        public MetaPtr CreateExportPointer(IUnityObjectBase asset) => new(GetExportID(asset));

        public UnityGuid ScenePathToGUID(string name) => default;

        public bool IsSceneDuplicate(int sceneID) => false;

        public AssetCollection File => file;

        public UnityVersion ExportVersion => file.Version;
    }
}
