using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.GUI.Web;
using AssetRipper.SourceGenerated;
using Microsoft.Data.Sqlite;
using Ruri.RipperHook.Endfield;

namespace Ruri.RipperHook.EndField;

public static class EndFieldAssetIndex
{
    private const int MaxStringsPerJob = 512;
    private const int MaxReferencesPerAsset = 512;
    private const int MaxTraversalDepth = 5;
    private const int MaxEnumerableItems = 128;
    private const int MaxQueryDeepParseJobs = 32;
    private const int LightweightCommitInterval = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly Regex UsefulStringRegex = new(
        @"(^_[A-Za-z][A-Za-z0-9_]{3,}$)|([A-Za-z0-9_/\.\-]+(?:\.shader|\.shadervariants|\.mat|\.mesh|\.prefab|\.png|\.tga|\.exr|\.jpg|\.jpeg|\.ab)$)|(Shader|Material|Texture|Mesh|Renderer|CAB-|svc_)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record BuildOptions(string GameRootOrDataDirectory, string IndexDbPath, int Parallel, bool DeepParse = true);

    public sealed record QueryOptions(string IndexDbPath, string Query, IReadOnlyList<string> TargetTypes, string? ReportOut);

    public sealed record BuildSummary(
        string Status,
        string DataDirectory,
        string IndexDbPath,
        string RunDirectory,
        int VfsFiles,
        int LogicalAssetBundles,
        int JobsParsed,
        int JobsSkipped,
        int JobsFailed,
        int AssetRecords,
        int ReferenceRecords,
        int StringRecords,
        string Note);

    public sealed record QuerySummary(
        string Status,
        string IndexDbPath,
        string Query,
        int CandidateCount,
        string? ReportPath);

    private sealed record LogicalFileRecord(
        string JobId,
        string SourceRoot,
        string BlockInfoPath,
        string GroupName,
        int CodeVersion,
        int Version,
        string ChunkMd5Name,
        string ChunkContentMd5,
        long ChunkLength,
        string ChunkPath,
        bool ChunkExists,
        string LogicalPath,
        string Extension,
        string FileNameHash,
        string FileChunkMd5Name,
        string FileDataMd5,
        ulong Offset,
        ulong Length,
        byte BlockType,
        bool UseEncrypt,
        ulong IvSeed,
        uint Reserved,
        EndFieldVfsBlock Block,
        EndFieldVfsChunk Chunk,
        EndFieldVfsFile File);

    private sealed record ManifestDocument(
        string RunDirectory,
        string DataDirectory,
        string IndexDbPath,
        DateTimeOffset CreatedAtUtc,
        int RequestedParallelism,
        string Note,
        IReadOnlyList<ManifestJob> Jobs);

    private sealed record ManifestJob(
        string Id,
        string Kind,
        string Status,
        string LogicalPath,
        string ChunkPath,
        ulong Offset,
        ulong Length,
        string OutputPath,
        string LogPath,
        string Owner,
        string RecordingOwner,
        IReadOnlyList<string> Validation);

    private sealed record JobImportStats(int Assets, int References, int Strings, bool Failed, string? Error);

    private sealed record LightweightImportStats(int Parsed, int Skipped, int Failed, int Strings);

    private sealed record QueryReport(
        string Query,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<string> TargetTypes,
        IReadOnlyList<QueryCandidate> Candidates,
        IReadOnlyList<StringHit> StringHits);

    private sealed record QueryCandidate(
        string AssetKey,
        string Name,
        string TypeName,
        long PathId,
        string Cab,
        string Container,
        string LogicalPath,
        string ChunkPath,
        long Offset,
        long Length,
        int Score,
        string Reason,
        IReadOnlyList<QueryAsset> RelatedAssets,
        IReadOnlyList<QueryReference> References,
        IReadOnlyList<string> CabDependencies);

    private sealed record QueryAsset(string AssetKey, string Name, string TypeName, long PathId, string Cab, string Container, string LogicalPath);

    private sealed record QueryReference(string Direction, string Kind, string FromAssetKey, string FromName, string ToAssetKey, string ToName, string ToTypeName);

    private sealed record StringHit(string LogicalPath, string Value, long Offset);

    public static BuildSummary Build(BuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameRootOrDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexDbPath);

        string dataDirectory = ResolveDataDirectory(options.GameRootOrDataDirectory);
        string dbPath = Path.GetFullPath(options.IndexDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? Directory.GetCurrentDirectory());

        string runDirectory = Path.Combine(Path.GetDirectoryName(dbPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(dbPath) + ".index-run");
        string outputsDirectory = Path.Combine(runDirectory, "outputs");
        string logsDirectory = Path.Combine(runDirectory, "logs");
        string stagingDirectory = Path.Combine(runDirectory, "staging");
        Directory.CreateDirectory(outputsDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(stagingDirectory);

        List<LogicalFileRecord> logicalFiles = EnumerateLogicalFiles(dataDirectory);
        List<LogicalFileRecord> assetBundles = logicalFiles
            .Where(static file => file.Extension.Equals(".ab", StringComparison.OrdinalIgnoreCase))
            .Where(static file => file.ChunkExists)
            .OrderBy(static file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AssignJobIds(assetBundles);
        WriteManifest(runDirectory, dataDirectory, dbPath, options.Parallel, assetBundles);

        using SqliteConnection connection = OpenDatabase(dbPath);
        EnsureSchema(connection);
        ReplaceVfsFiles(connection, logicalFiles);

        if (!options.DeepParse)
        {
            LightweightImportStats stats = BuildLightweightStringIndex(connection, assetBundles);
            return new BuildSummary(
                stats.Failed > 0 ? "partial" : "ok",
                dataDirectory,
                dbPath,
                runDirectory,
                logicalFiles.Count,
                assetBundles.Count,
                stats.Parsed,
                stats.Skipped,
                stats.Failed,
                0,
                0,
                stats.Strings,
                "Lightweight index only: VFS files and useful bundle strings are indexed. Query auto-deep-parses matching bundles on demand; pass --deep-asset-index for full AssetRipper parsing.");
        }

        int parsed = 0;
        int skipped = 0;
        int failed = 0;
        int assets = 0;
        int references = 0;
        int strings = 0;

        if (options.Parallel > 1)
        {
            Console.Error.WriteLine("[EndField] asset-index: --parallel is recorded in the manifest; v1 parses AssetRipper jobs serially because GameFileLoader is process-global.");
        }

        int visited = 0;
        foreach (LogicalFileRecord job in assetBundles)
        {
            visited++;
            string inputHash = BuildInputHash(job);
            if (IsJobCurrent(connection, job.JobId, inputHash))
            {
                skipped++;
                if (visited % 1000 == 0)
                {
                    Console.Error.WriteLine($"[EndField] asset-index progress {visited}/{assetBundles.Count}: parsed={parsed} skipped={skipped} failed={failed}");
                }
                continue;
            }

            MarkJobStarted(connection, job, inputHash);
            string outputPath = GetJobOutputPath(outputsDirectory, job.JobId);
            string logPath = GetJobLogPath(logsDirectory, job.JobId);
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProcessJob(job, outputPath, logPath, stagingDirectory);
            stopwatch.Stop();

            JobImportStats stats;
            try
            {
                stats = ValidateAndImportJobOutput(connection, job, outputPath);
            }
            catch (Exception ex)
            {
                stats = new JobImportStats(0, 0, 0, true, $"{ex.GetType().Name}: {ex.Message}");
            }

            CompleteJob(connection, job.JobId, stats.Failed ? "failed" : "success", stopwatch.ElapsedMilliseconds, stats.Error);
            if (stats.Failed)
            {
                failed++;
            }
            else
            {
                parsed++;
                assets += stats.Assets;
                references += stats.References;
                strings += stats.Strings;
            }

            if (visited % 100 == 0)
            {
                Console.Error.WriteLine($"[EndField] asset-index progress {visited}/{assetBundles.Count}: parsed={parsed} skipped={skipped} failed={failed}");
            }
        }

        return new BuildSummary(
            failed == 0 ? "ok" : "partial",
            dataDirectory,
            dbPath,
            runDirectory,
            logicalFiles.Count,
            assetBundles.Count,
            parsed,
            skipped,
            failed,
            assets,
            references,
            strings,
            "SQLite is written only by the parent build flow; job JSONL is kept under the run directory for validation and retry.");
    }

    public static QuerySummary Query(QueryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Query);

        string dbPath = Path.GetFullPath(options.IndexDbPath);
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException("Index database not found.", dbPath);
        }

        string[] targetTypes = NormalizeTargetTypes(options.TargetTypes);
        using SqliteConnection connection = OpenDatabase(dbPath);
        EnsureSchema(connection);

        QueryReport report = BuildQueryReport(connection, options.Query, targetTypes);
        if (DeepenQueryHits(connection, dbPath, options.Query, report) > 0)
        {
            report = BuildQueryReport(connection, options.Query, targetTypes);
        }

        string? reportPath = null;
        if (!string.IsNullOrWhiteSpace(options.ReportOut))
        {
            reportPath = Path.GetFullPath(options.ReportOut);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        }

        return new QuerySummary(
            report.Candidates.Count == 0 && report.StringHits.Count == 0 ? "not_found" : "ok",
            dbPath,
            options.Query,
            report.Candidates.Count,
            reportPath);
    }

    private static void AssignJobIds(List<LogicalFileRecord> jobs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            LogicalFileRecord job = jobs[i];
            jobs[i] = job with { JobId = $"parse-ab-{i + 1:D6}" };
        }
    }

    private static void WriteManifest(string runDirectory, string dataDirectory, string dbPath, int parallel, IReadOnlyList<LogicalFileRecord> jobs)
    {
        ManifestDocument manifest = new(
            runDirectory,
            dataDirectory,
            dbPath,
            DateTimeOffset.UtcNow,
            Math.Max(1, parallel),
            "Parent-owned manifest. Workers may write only their output/log/staging paths; SQLite is parent-owned.",
            jobs.Select(job => new ManifestJob(
                job.JobId,
                "parse-logical-ab",
                "pending",
                job.LogicalPath,
                job.ChunkPath,
                job.Offset,
                job.Length,
                Path.Combine(runDirectory, "outputs", job.JobId + ".jsonl"),
                Path.Combine(runDirectory, "logs", job.JobId + ".log"),
                "subagent",
                "parent",
                ["jsonl-schema", "asset-count", "no-shared-write"])).ToArray());

        File.WriteAllText(Path.Combine(runDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
    }

    private static List<LogicalFileRecord> EnumerateLogicalFiles(string dataDirectory)
    {
        List<LogicalFileRecord> result = new();
        foreach ((string sourceRoot, string vfsRoot) in EnumerateVfsRoots(dataDirectory))
        {
            foreach (string blockInfoPath in Directory.EnumerateFiles(vfsRoot, "*.blc", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!EndField_0_8_25_Vfs.TryParseBlockInfo(blockInfoPath, out EndFieldVfsBlock block, out string error))
                {
                    Console.Error.WriteLine($"[EndField] asset-index parse-error source={sourceRoot} file={Path.GetFileName(blockInfoPath)} reason={error}");
                    continue;
                }

                foreach (EndFieldVfsChunk chunk in block.Chunks)
                {
                    string chunkPath = Path.Combine(Path.GetDirectoryName(block.BlockInfoPath) ?? string.Empty, chunk.Md5Name + ".chk");
                    bool chunkExists = File.Exists(chunkPath);
                    foreach (EndFieldVfsFile file in chunk.Files)
                    {
                        result.Add(new LogicalFileRecord(
                            string.Empty,
                            sourceRoot,
                            block.BlockInfoPath,
                            block.GroupName,
                            block.CodeVersion,
                            block.Version,
                            chunk.Md5Name,
                            chunk.ContentMd5,
                            chunk.Length,
                            chunkPath,
                            chunkExists,
                            file.FileName,
                            Path.GetExtension(file.FileName),
                            file.FileNameHash.ToString("X16"),
                            file.FileChunkMd5Name,
                            file.FileDataMd5,
                            file.Offset,
                            file.Length,
                            file.BlockType,
                            file.UseEncrypt,
                            file.IvSeed,
                            file.Reserved,
                            block,
                            chunk,
                            file));
                    }
                }
            }
        }

        return result;
    }

    private static void ProcessJob(LogicalFileRecord job, string outputPath, string logPath, string stagingRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Directory.GetCurrentDirectory());
        string jobStagingDirectory = Path.Combine(stagingRoot, job.JobId);
        Directory.CreateDirectory(jobStagingDirectory);

        using StreamWriter output = new(outputPath, append: false, Encoding.UTF8);
        using StreamWriter log = new(logPath, append: false, Encoding.UTF8);

        try
        {
            WriteJobHeader(output, job);
            if (!EndField_0_8_25_Vfs.TryReadFile(job.Block, job.Chunk, job.File, out byte[] data, out string readError))
            {
                WriteJobError(output, job, "read", readError, null);
                log.WriteLine(readError);
                return;
            }

            foreach ((long offset, string value) in ExtractUsefulStrings(data).Take(MaxStringsPerJob))
            {
                WriteRecord(output, new Dictionary<string, object?>
                {
                    ["record_kind"] = "string",
                    ["job_id"] = job.JobId,
                    ["logical_path"] = job.LogicalPath,
                    ["offset"] = offset,
                    ["value"] = value,
                });
            }

            string stagedPath = Path.Combine(jobStagingDirectory, SanitizeFileName(Path.GetFileName(job.LogicalPath)));
            File.WriteAllBytes(stagedPath, data);

            GameFileLoader.LoadAndProcess([stagedPath]);
            if (!GameFileLoader.IsLoaded)
            {
                WriteJobError(output, job, "assetripper", "GameFileLoader did not load any asset data.", null);
                return;
            }

            int assetCount = 0;
            int referenceCount = 0;
            foreach (AssetCollection collection in GameFileLoader.GameBundle.FetchAssetCollections())
            {
                string cab = string.IsNullOrWhiteSpace(collection.Name) ? Path.GetFileName(job.LogicalPath) : collection.Name;
                foreach (AssetCollection? dependency in collection.Dependencies.Skip(1))
                {
                    if (dependency is null || string.IsNullOrWhiteSpace(dependency.Name))
                    {
                        continue;
                    }

                    WriteRecord(output, new Dictionary<string, object?>
                    {
                        ["record_kind"] = "cab_dependency",
                        ["job_id"] = job.JobId,
                        ["logical_path"] = job.LogicalPath,
                        ["from_cab"] = cab,
                        ["to_cab"] = dependency.Name,
                    });
                }

                foreach (IUnityObjectBase asset in collection)
                {
                    string assetKey = BuildAssetKey(job.LogicalPath, asset.PathID);
                    WriteRecord(output, new Dictionary<string, object?>
                    {
                        ["record_kind"] = "asset",
                        ["job_id"] = job.JobId,
                        ["logical_path"] = job.LogicalPath,
                        ["source_root"] = job.SourceRoot,
                        ["chunk_path"] = job.ChunkPath,
                        ["offset"] = (long)job.Offset,
                        ["length"] = (long)job.Length,
                        ["asset_key"] = assetKey,
                        ["name"] = asset.GetBestName(),
                        ["type_id"] = asset.ClassID,
                        ["type_name"] = GetClassIdName(asset.ClassID, asset.ClassName),
                        ["path_id"] = asset.PathID,
                        ["cab"] = cab,
                        ["container"] = asset.OriginalPath ?? asset.AssetBundleName ?? string.Empty,
                    });
                    assetCount++;

                    foreach (ReferenceRecord reference in ReferenceExtractor.Extract(asset, assetKey, job.LogicalPath, cab))
                    {
                        WriteRecord(output, new Dictionary<string, object?>
                        {
                            ["record_kind"] = "asset_reference",
                            ["job_id"] = job.JobId,
                            ["logical_path"] = job.LogicalPath,
                            ["from_asset_key"] = reference.FromAssetKey,
                            ["from_name"] = reference.FromName,
                            ["from_type_name"] = reference.FromTypeName,
                            ["from_path_id"] = reference.FromPathId,
                            ["to_asset_key"] = reference.ToAssetKey,
                            ["to_name"] = reference.ToName,
                            ["to_type_name"] = reference.ToTypeName,
                            ["to_path_id"] = reference.ToPathId,
                            ["to_file_id"] = reference.ToFileId,
                            ["reference_kind"] = reference.ReferenceKind,
                        });
                        referenceCount++;
                    }
                }
            }

            WriteRecord(output, new Dictionary<string, object?>
            {
                ["record_kind"] = "job_summary",
                ["job_id"] = job.JobId,
                ["logical_path"] = job.LogicalPath,
                ["assets"] = assetCount,
                ["references"] = referenceCount,
            });
        }
        catch (Exception ex)
        {
            WriteJobError(output, job, "exception", $"{ex.GetType().Name}: {ex.Message}", ex.ToString());
            log.WriteLine(ex);
        }
        finally
        {
            GameFileLoader.Reset();
            try
            {
                if (Directory.Exists(jobStagingDirectory))
                {
                    Directory.Delete(jobStagingDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                log.WriteLine($"staging cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void WriteJobHeader(StreamWriter output, LogicalFileRecord job)
    {
        WriteRecord(output, new Dictionary<string, object?>
        {
            ["record_kind"] = "job_header",
            ["job_id"] = job.JobId,
            ["logical_path"] = job.LogicalPath,
            ["chunk_path"] = job.ChunkPath,
            ["offset"] = (long)job.Offset,
            ["length"] = (long)job.Length,
        });
    }

    private static void WriteJobError(StreamWriter output, LogicalFileRecord job, string stage, string error, string? stack)
    {
        WriteRecord(output, new Dictionary<string, object?>
        {
            ["record_kind"] = "job_error",
            ["job_id"] = job.JobId,
            ["logical_path"] = job.LogicalPath,
            ["stage"] = stage,
            ["error"] = error,
            ["stack"] = stack,
        });
    }

    private static void WriteRecord(StreamWriter output, Dictionary<string, object?> record)
    {
        output.WriteLine(JsonSerializer.Serialize(record));
    }

    private static LightweightImportStats BuildLightweightStringIndex(SqliteConnection connection, IReadOnlyList<LogicalFileRecord> jobs)
    {
        int parsed = 0;
        int skipped = 0;
        int stringCount = 0;
        int failed = 0;
        int batchCount = 0;
        SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            foreach (LogicalFileRecord job in jobs)
            {
                string inputHash = BuildInputHash(job);
                string? currentStatus = GetCurrentJobStatus(connection, job.JobId, inputHash, transaction);
                if (currentStatus is not null &&
                    (currentStatus.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                     currentStatus.Equals("lightweight", StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                DeleteStrings(connection, transaction, job.LogicalPath);
                try
                {
                    if (!EndField_0_8_25_Vfs.TryReadFile(job.Block, job.Chunk, job.File, out byte[] data, out string readError))
                    {
                        CompleteJobInTransaction(connection, transaction, job, inputHash, "failed", stopwatch.ElapsedMilliseconds, readError);
                        failed++;
                    }
                    else
                    {
                        int jobStrings = 0;
                        foreach ((long offset, string value) in ExtractUsefulStrings(data).Take(MaxStringsPerJob))
                        {
                            InsertString(connection, transaction, job.JobId, job.LogicalPath, offset, value);
                            jobStrings++;
                        }

                        CompleteJobInTransaction(connection, transaction, job, inputHash, "lightweight", stopwatch.ElapsedMilliseconds, null);
                        parsed++;
                        stringCount += jobStrings;
                    }
                }
                catch (Exception ex)
                {
                    CompleteJobInTransaction(connection, transaction, job, inputHash, "failed", stopwatch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
                    failed++;
                }

                batchCount++;
                if (batchCount >= LightweightCommitInterval)
                {
                    transaction.Commit();
                    transaction.Dispose();
                    transaction = connection.BeginTransaction();
                    batchCount = 0;
                }
            }

            transaction.Commit();
        }
        finally
        {
            transaction.Dispose();
        }

        return new LightweightImportStats(parsed, skipped, failed, stringCount);
    }

    private static JobImportStats ValidateAndImportJobOutput(SqliteConnection connection, LogicalFileRecord job, string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return new JobImportStats(0, 0, 0, true, "job output missing");
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        DeleteJobRows(connection, transaction, job.LogicalPath);

        int assetCount = 0;
        int referenceCount = 0;
        int stringCount = 0;
        string? jobError = null;

        foreach (string line in File.ReadLines(outputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string recordKind = RequireString(root, "record_kind");
            string jobId = RequireString(root, "job_id");
            string logicalPath = RequireString(root, "logical_path");
            if (!string.Equals(jobId, job.JobId, StringComparison.Ordinal) || !string.Equals(logicalPath, job.LogicalPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"record does not belong to job {job.JobId}: {line}");
            }

            switch (recordKind)
            {
                case "asset":
                    InsertAsset(connection, transaction, root);
                    assetCount++;
                    break;
                case "cab_dependency":
                    InsertCabDependency(connection, transaction, root);
                    break;
                case "asset_reference":
                    InsertAssetReference(connection, transaction, root);
                    referenceCount++;
                    break;
                case "string":
                    InsertString(connection, transaction, root);
                    stringCount++;
                    break;
                case "job_error":
                    jobError = OptionalString(root, "error") ?? "job error";
                    break;
                case "job_header":
                case "job_summary":
                    break;
                default:
                    throw new InvalidDataException($"unknown record kind: {recordKind}");
            }
        }

        transaction.Commit();
        return new JobImportStats(assetCount, referenceCount, stringCount, jobError is not null, jobError);
    }

    private static QueryReport BuildQueryReport(SqliteConnection connection, string query, IReadOnlyList<string> targetTypes)
    {
        List<QueryCandidate> candidates = new();
        string like = "%" + query + "%";
        string typeClause = BuildTypeClause(targetTypes, "type_name");
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT asset_key, name, type_name, path_id, cab, container, logical_path, chunk_path, offset, length,
                       CASE
                         WHEN lower(name) = lower($query) THEN 100
                         WHEN lower(name) LIKE lower($like) THEN 80
                         WHEN lower(container) LIKE lower($like) THEN 70
                         WHEN lower(cab) LIKE lower($like) THEN 65
                         WHEN lower(logical_path) LIKE lower($like) THEN 60
                         ELSE 10
                       END AS score,
                       CASE
                         WHEN lower(name) = lower($query) THEN 'exact-name'
                         WHEN lower(name) LIKE lower($like) THEN 'name-contains'
                         WHEN lower(container) LIKE lower($like) THEN 'container-contains'
                         WHEN lower(cab) LIKE lower($like) THEN 'cab-contains'
                         WHEN lower(logical_path) LIKE lower($like) THEN 'logical-path-contains'
                         ELSE 'candidate'
                       END AS reason
                FROM assets
                WHERE (lower(name) = lower($query)
                   OR lower(name) LIKE lower($like)
                   OR lower(container) LIKE lower($like)
                   OR lower(cab) LIKE lower($like)
                   OR lower(logical_path) LIKE lower($like))
                  AND {typeClause}
                ORDER BY score DESC, name COLLATE NOCASE
                LIMIT 50
                """;
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$like", like);
            AddTypeParameters(command, targetTypes);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string assetKey = reader.GetString(0);
                string logicalPath = reader.GetString(6);
                string cab = reader.GetString(4);
                candidates.Add(new QueryCandidate(
                    assetKey,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    cab,
                    reader.GetString(5),
                    logicalPath,
                    reader.GetString(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt32(10),
                    reader.GetString(11),
                    LoadRelatedAssets(connection, logicalPath, cab, assetKey, targetTypes),
                    LoadReferences(connection, assetKey),
                    LoadCabDependencies(connection, cab)));
            }
        }

        List<StringHit> stringHits = new();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT logical_path, value, offset
                FROM strings
                WHERE lower(value) LIKE lower($like)
                ORDER BY logical_path COLLATE NOCASE, offset
                LIMIT 100
                """;
            command.Parameters.AddWithValue("$like", like);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                stringHits.Add(new StringHit(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        return new QueryReport(query, DateTimeOffset.UtcNow, targetTypes.ToArray(), candidates, stringHits);
    }

    private static int DeepenQueryHits(SqliteConnection connection, string dbPath, string query, QueryReport report)
    {
        string runDirectory = Path.Combine(Path.GetDirectoryName(dbPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(dbPath) + ".index-run");
        string outputsDirectory = Path.Combine(runDirectory, "outputs");
        string logsDirectory = Path.Combine(runDirectory, "logs");
        string stagingDirectory = Path.Combine(runDirectory, "staging");
        Directory.CreateDirectory(outputsDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(stagingDirectory);

        List<string> logicalPaths = report.StringHits
            .Select(static hit => hit.LogicalPath)
            .Concat(LoadVfsQueryHits(connection, query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueryDeepParseJobs)
            .ToList();

        int parsed = 0;
        foreach (string logicalPath in logicalPaths)
        {
            LogicalFileRecord? job = LoadLogicalFileFromDatabase(connection, logicalPath);
            if (job is null)
            {
                continue;
            }

            string inputHash = BuildInputHash(job);
            if (IsJobCurrent(connection, job.JobId, inputHash))
            {
                continue;
            }

            MarkJobStarted(connection, job, inputHash);
            string outputPath = GetJobOutputPath(outputsDirectory, job.JobId);
            string logPath = GetJobLogPath(logsDirectory, job.JobId);
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProcessJob(job, outputPath, logPath, stagingDirectory);
            stopwatch.Stop();

            JobImportStats stats;
            try
            {
                stats = ValidateAndImportJobOutput(connection, job, outputPath);
            }
            catch (Exception ex)
            {
                stats = new JobImportStats(0, 0, 0, true, $"{ex.GetType().Name}: {ex.Message}");
            }

            CompleteJob(connection, job.JobId, stats.Failed ? "failed" : "success", stopwatch.ElapsedMilliseconds, stats.Error);
            if (!stats.Failed)
            {
                parsed++;
            }
        }

        return parsed;
    }

    private static IReadOnlyList<string> LoadVfsQueryHits(SqliteConnection connection, string query)
    {
        List<string> result = new();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT logical_path
            FROM vfs_files
            WHERE extension = '.ab'
              AND chunk_exists = 1
              AND lower(logical_path) LIKE lower($like)
            ORDER BY logical_path COLLATE NOCASE
            LIMIT 100
            """;
        command.Parameters.AddWithValue("$like", "%" + query + "%");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static LogicalFileRecord? LoadLogicalFileFromDatabase(SqliteConnection connection, string logicalPath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.source_root, v.block_info_path, v.group_name, v.code_version, v.version,
                   v.chunk_md5_name, v.chunk_content_md5, v.chunk_length, v.chunk_path, v.chunk_exists,
                   v.logical_path, v.extension, v.file_name_hash, v.file_chunk_md5_name, v.file_data_md5,
                   v.offset, v.length, v.block_type, v.use_encrypt, v.iv_seed, v.reserved,
                   COALESCE(j.job_id, '')
            FROM vfs_files v
            LEFT JOIN index_jobs j ON j.logical_path = v.logical_path
            WHERE v.logical_path = $logical_path
              AND v.extension = '.ab'
              AND v.chunk_exists = 1
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$logical_path", logicalPath);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string blockInfoPath = reader.GetString(1);
        string chunkMd5Name = reader.GetString(5);
        ulong offset = (ulong)reader.GetInt64(15);
        ulong length = (ulong)reader.GetInt64(16);
        if (!EndField_0_8_25_Vfs.TryParseBlockInfo(blockInfoPath, out EndFieldVfsBlock block, out _))
        {
            return null;
        }

        EndFieldVfsChunk? chunk = block.Chunks.FirstOrDefault(item => item.Md5Name.Equals(chunkMd5Name, StringComparison.OrdinalIgnoreCase));
        if (chunk is null)
        {
            return null;
        }

        EndFieldVfsFile? file = chunk.Files.FirstOrDefault(item =>
            item.FileName.Equals(logicalPath, StringComparison.OrdinalIgnoreCase) &&
            item.Offset == offset &&
            item.Length == length);
        if (file is null)
        {
            return null;
        }

        string jobId = reader.GetString(21);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            jobId = BuildStableQueryJobId(logicalPath);
        }

        return new LogicalFileRecord(
            jobId,
            reader.GetString(0),
            blockInfoPath,
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            chunkMd5Name,
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetInt64(9) != 0,
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            offset,
            length,
            (byte)reader.GetInt64(17),
            reader.GetInt64(18) != 0,
            string.IsNullOrWhiteSpace(reader.GetString(19)) ? 0UL : Convert.ToUInt64(reader.GetString(19), 16),
            (uint)reader.GetInt64(20),
            block,
            chunk,
            file);
    }

    private static IReadOnlyList<QueryAsset> LoadRelatedAssets(SqliteConnection connection, string logicalPath, string cab, string assetKey, IReadOnlyList<string> targetTypes)
    {
        List<QueryAsset> result = new();
        string typeClause = BuildTypeClause(targetTypes, "type_name");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT asset_key, name, type_name, path_id, cab, container, logical_path
            FROM assets
            WHERE asset_key <> $asset_key
              AND (logical_path = $logical_path OR cab = $cab)
              AND {typeClause}
            ORDER BY CASE WHEN cab = $cab THEN 0 ELSE 1 END, type_name, name COLLATE NOCASE
            LIMIT 100
            """;
        command.Parameters.AddWithValue("$asset_key", assetKey);
        command.Parameters.AddWithValue("$logical_path", logicalPath);
        command.Parameters.AddWithValue("$cab", cab);
        AddTypeParameters(command, targetTypes);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new QueryAsset(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }
        return result;
    }

    private static IReadOnlyList<QueryReference> LoadReferences(SqliteConnection connection, string assetKey)
    {
        List<QueryReference> result = new();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'out', reference_kind, from_asset_key, from_name, to_asset_key, to_name, to_type_name
            FROM asset_references
            WHERE from_asset_key = $asset_key
            UNION ALL
            SELECT 'in', reference_kind, from_asset_key, from_name, to_asset_key, to_name, to_type_name
            FROM asset_references
            WHERE to_asset_key = $asset_key
            LIMIT 100
            """;
        command.Parameters.AddWithValue("$asset_key", assetKey);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new QueryReference(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }
        return result;
    }

    private static IReadOnlyList<string> LoadCabDependencies(SqliteConnection connection, string cab)
    {
        List<string> result = new();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT to_cab
            FROM cab_dependencies
            WHERE from_cab = $cab
            ORDER BY to_cab COLLATE NOCASE
            LIMIT 200
            """;
        command.Parameters.AddWithValue("$cab", cab);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static string BuildTypeClause(IReadOnlyList<string> targetTypes, string column)
    {
        if (targetTypes.Count == 0)
        {
            return "1 = 1";
        }

        return $"{column} IN ({string.Join(", ", Enumerable.Range(0, targetTypes.Count).Select(i => "$type" + i))})";
    }

    private static void AddTypeParameters(SqliteCommand command, IReadOnlyList<string> targetTypes)
    {
        for (int i = 0; i < targetTypes.Count; i++)
        {
            command.Parameters.AddWithValue("$type" + i, targetTypes[i]);
        }
    }

    private static string[] NormalizeTargetTypes(IReadOnlyList<string> targetTypes)
    {
        string[] effective = targetTypes.Count == 0 ? ["Material", "Texture2D", "Mesh"] : targetTypes.ToArray();
        return effective
            .Select(static type => type.Trim())
            .Where(static type => type.Length > 0)
            .Select(static type => Enum.TryParse<ClassIDType>(type, ignoreCase: true, out ClassIDType classId) ? classId.ToString() : type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SqliteConnection OpenDatabase(string dbPath)
    {
        SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS vfs_files (
                logical_path TEXT NOT NULL,
                source_root TEXT NOT NULL,
                block_info_path TEXT NOT NULL,
                group_name TEXT NOT NULL,
                code_version INTEGER NOT NULL,
                version INTEGER NOT NULL,
                chunk_md5_name TEXT NOT NULL,
                chunk_content_md5 TEXT NOT NULL,
                chunk_length INTEGER NOT NULL,
                chunk_path TEXT NOT NULL,
                chunk_exists INTEGER NOT NULL,
                extension TEXT NOT NULL,
                file_name_hash TEXT NOT NULL,
                file_chunk_md5_name TEXT NOT NULL,
                file_data_md5 TEXT NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                block_type INTEGER NOT NULL,
                use_encrypt INTEGER NOT NULL,
                iv_seed TEXT NOT NULL,
                reserved INTEGER NOT NULL,
                PRIMARY KEY (source_root, logical_path, chunk_path, offset, length)
            );

            CREATE TABLE IF NOT EXISTS assets (
                asset_key TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                source_root TEXT NOT NULL,
                chunk_path TEXT NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                name TEXT NOT NULL,
                type_id INTEGER NOT NULL,
                type_name TEXT NOT NULL,
                path_id INTEGER NOT NULL,
                cab TEXT NOT NULL,
                container TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cab_dependencies (
                job_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                from_cab TEXT NOT NULL,
                to_cab TEXT NOT NULL,
                PRIMARY KEY (logical_path, from_cab, to_cab)
            );

            CREATE TABLE IF NOT EXISTS asset_references (
                job_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                from_asset_key TEXT NOT NULL,
                from_name TEXT NOT NULL,
                from_type_name TEXT NOT NULL,
                from_path_id INTEGER NOT NULL,
                to_asset_key TEXT NOT NULL,
                to_name TEXT NOT NULL,
                to_type_name TEXT NOT NULL,
                to_path_id INTEGER NOT NULL,
                to_file_id INTEGER NOT NULL,
                reference_kind TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strings (
                job_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                offset INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (logical_path, offset, value)
            );

            CREATE TABLE IF NOT EXISTS index_jobs (
                job_id TEXT PRIMARY KEY,
                logical_path TEXT NOT NULL,
                chunk_path TEXT NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                input_hash TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at_utc TEXT,
                finished_at_utc TEXT,
                elapsed_ms INTEGER NOT NULL DEFAULT 0,
                error TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_assets_name ON assets(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_assets_type ON assets(type_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_assets_logical ON assets(logical_path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_assets_cab ON assets(cab COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_refs_from ON asset_references(from_asset_key);
            CREATE INDEX IF NOT EXISTS idx_refs_to ON asset_references(to_asset_key);
            CREATE INDEX IF NOT EXISTS idx_strings_value ON strings(value COLLATE NOCASE);
            """);
    }

    private static void ReplaceVfsFiles(SqliteConnection connection, IReadOnlyList<LogicalFileRecord> files)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, "DELETE FROM vfs_files", transaction);
        foreach (LogicalFileRecord file in files)
        {
            ExecuteNonQuery(connection, """
                INSERT OR REPLACE INTO vfs_files (
                    logical_path, source_root, block_info_path, group_name, code_version, version,
                    chunk_md5_name, chunk_content_md5, chunk_length, chunk_path, chunk_exists,
                    extension, file_name_hash, file_chunk_md5_name, file_data_md5, offset, length,
                    block_type, use_encrypt, iv_seed, reserved)
                VALUES (
                    $logical_path, $source_root, $block_info_path, $group_name, $code_version, $version,
                    $chunk_md5_name, $chunk_content_md5, $chunk_length, $chunk_path, $chunk_exists,
                    $extension, $file_name_hash, $file_chunk_md5_name, $file_data_md5, $offset, $length,
                    $block_type, $use_encrypt, $iv_seed, $reserved)
                """, transaction, new Dictionary<string, object?>
            {
                ["$logical_path"] = file.LogicalPath,
                ["$source_root"] = file.SourceRoot,
                ["$block_info_path"] = file.BlockInfoPath,
                ["$group_name"] = file.GroupName,
                ["$code_version"] = file.CodeVersion,
                ["$version"] = file.Version,
                ["$chunk_md5_name"] = file.ChunkMd5Name,
                ["$chunk_content_md5"] = file.ChunkContentMd5,
                ["$chunk_length"] = file.ChunkLength,
                ["$chunk_path"] = file.ChunkPath,
                ["$chunk_exists"] = file.ChunkExists ? 1 : 0,
                ["$extension"] = file.Extension,
                ["$file_name_hash"] = file.FileNameHash,
                ["$file_chunk_md5_name"] = file.FileChunkMd5Name,
                ["$file_data_md5"] = file.FileDataMd5,
                ["$offset"] = (long)file.Offset,
                ["$length"] = (long)file.Length,
                ["$block_type"] = file.BlockType,
                ["$use_encrypt"] = file.UseEncrypt ? 1 : 0,
                ["$iv_seed"] = file.UseEncrypt ? file.IvSeed.ToString("X16") : string.Empty,
                ["$reserved"] = file.Reserved,
            });
        }
        transaction.Commit();
    }

    private static bool IsJobCurrent(SqliteConnection connection, string jobId, string inputHash)
    {
        string? status = GetCurrentJobStatus(connection, jobId, inputHash, null);
        return status is not null && status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentJobStatus(SqliteConnection connection, string jobId, string inputHash, SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status FROM index_jobs WHERE job_id = $job_id AND input_hash = $input_hash";
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$input_hash", inputHash);
        object? result = command.ExecuteScalar();
        return result as string;
    }

    private static void MarkJobStarted(SqliteConnection connection, LogicalFileRecord job, string inputHash)
    {
        ExecuteNonQuery(connection, """
            INSERT INTO index_jobs (job_id, logical_path, chunk_path, offset, length, input_hash, status, started_at_utc, elapsed_ms, error)
            VALUES ($job_id, $logical_path, $chunk_path, $offset, $length, $input_hash, 'running', $started_at_utc, 0, NULL)
            ON CONFLICT(job_id) DO UPDATE SET
                logical_path = excluded.logical_path,
                chunk_path = excluded.chunk_path,
                offset = excluded.offset,
                length = excluded.length,
                input_hash = excluded.input_hash,
                status = 'running',
                started_at_utc = excluded.started_at_utc,
                finished_at_utc = NULL,
                elapsed_ms = 0,
                error = NULL
            """, null, new Dictionary<string, object?>
        {
            ["$job_id"] = job.JobId,
            ["$logical_path"] = job.LogicalPath,
            ["$chunk_path"] = job.ChunkPath,
            ["$offset"] = (long)job.Offset,
            ["$length"] = (long)job.Length,
            ["$input_hash"] = inputHash,
            ["$started_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    private static void CompleteJob(SqliteConnection connection, string jobId, string status, long elapsedMs, string? error)
    {
        ExecuteNonQuery(connection, """
            UPDATE index_jobs
            SET status = $status,
                finished_at_utc = $finished_at_utc,
                elapsed_ms = $elapsed_ms,
                error = $error
            WHERE job_id = $job_id
            """, null, new Dictionary<string, object?>
        {
            ["$job_id"] = jobId,
            ["$status"] = status,
            ["$finished_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["$elapsed_ms"] = elapsedMs,
            ["$error"] = error,
        });
    }

    private static void CompleteJobInTransaction(SqliteConnection connection, SqliteTransaction transaction, LogicalFileRecord job, string inputHash, string status, long elapsedMs, string? error)
    {
        ExecuteNonQuery(connection, """
            INSERT INTO index_jobs (job_id, logical_path, chunk_path, offset, length, input_hash, status, started_at_utc, finished_at_utc, elapsed_ms, error)
            VALUES ($job_id, $logical_path, $chunk_path, $offset, $length, $input_hash, $status, $started_at_utc, $finished_at_utc, $elapsed_ms, $error)
            ON CONFLICT(job_id) DO UPDATE SET
                logical_path = excluded.logical_path,
                chunk_path = excluded.chunk_path,
                offset = excluded.offset,
                length = excluded.length,
                input_hash = excluded.input_hash,
                status = excluded.status,
                started_at_utc = excluded.started_at_utc,
                finished_at_utc = excluded.finished_at_utc,
                elapsed_ms = excluded.elapsed_ms,
                error = excluded.error
            """, transaction, new Dictionary<string, object?>
        {
            ["$job_id"] = job.JobId,
            ["$logical_path"] = job.LogicalPath,
            ["$chunk_path"] = job.ChunkPath,
            ["$offset"] = (long)job.Offset,
            ["$length"] = (long)job.Length,
            ["$input_hash"] = inputHash,
            ["$status"] = status,
            ["$started_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["$finished_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["$elapsed_ms"] = elapsedMs,
            ["$error"] = error,
        });
    }

    private static void DeleteJobRows(SqliteConnection connection, SqliteTransaction transaction, string logicalPath)
    {
        foreach (string table in new[] { "assets", "cab_dependencies", "asset_references", "strings" })
        {
            ExecuteNonQuery(connection, $"DELETE FROM {table} WHERE logical_path = $logical_path", transaction, new Dictionary<string, object?>
            {
                ["$logical_path"] = logicalPath,
            });
        }
    }

    private static void DeleteStrings(SqliteConnection connection, SqliteTransaction transaction, string logicalPath)
    {
        ExecuteNonQuery(connection, "DELETE FROM strings WHERE logical_path = $logical_path", transaction, new Dictionary<string, object?>
        {
            ["$logical_path"] = logicalPath,
        });
    }

    private static void InsertAsset(SqliteConnection connection, SqliteTransaction transaction, JsonElement root)
    {
        ExecuteNonQuery(connection, """
            INSERT OR REPLACE INTO assets (
                asset_key, job_id, logical_path, source_root, chunk_path, offset, length,
                name, type_id, type_name, path_id, cab, container)
            VALUES (
                $asset_key, $job_id, $logical_path, $source_root, $chunk_path, $offset, $length,
                $name, $type_id, $type_name, $path_id, $cab, $container)
            """, transaction, new Dictionary<string, object?>
        {
            ["$asset_key"] = RequireString(root, "asset_key"),
            ["$job_id"] = RequireString(root, "job_id"),
            ["$logical_path"] = RequireString(root, "logical_path"),
            ["$source_root"] = RequireString(root, "source_root"),
            ["$chunk_path"] = RequireString(root, "chunk_path"),
            ["$offset"] = RequireInt64(root, "offset"),
            ["$length"] = RequireInt64(root, "length"),
            ["$name"] = RequireString(root, "name"),
            ["$type_id"] = RequireInt32(root, "type_id"),
            ["$type_name"] = RequireString(root, "type_name"),
            ["$path_id"] = RequireInt64(root, "path_id"),
            ["$cab"] = RequireString(root, "cab"),
            ["$container"] = OptionalString(root, "container") ?? string.Empty,
        });
    }

    private static void InsertCabDependency(SqliteConnection connection, SqliteTransaction transaction, JsonElement root)
    {
        ExecuteNonQuery(connection, """
            INSERT OR IGNORE INTO cab_dependencies (job_id, logical_path, from_cab, to_cab)
            VALUES ($job_id, $logical_path, $from_cab, $to_cab)
            """, transaction, new Dictionary<string, object?>
        {
            ["$job_id"] = RequireString(root, "job_id"),
            ["$logical_path"] = RequireString(root, "logical_path"),
            ["$from_cab"] = RequireString(root, "from_cab"),
            ["$to_cab"] = RequireString(root, "to_cab"),
        });
    }

    private static void InsertAssetReference(SqliteConnection connection, SqliteTransaction transaction, JsonElement root)
    {
        ExecuteNonQuery(connection, """
            INSERT INTO asset_references (
                job_id, logical_path, from_asset_key, from_name, from_type_name, from_path_id,
                to_asset_key, to_name, to_type_name, to_path_id, to_file_id, reference_kind)
            VALUES (
                $job_id, $logical_path, $from_asset_key, $from_name, $from_type_name, $from_path_id,
                $to_asset_key, $to_name, $to_type_name, $to_path_id, $to_file_id, $reference_kind)
            """, transaction, new Dictionary<string, object?>
        {
            ["$job_id"] = RequireString(root, "job_id"),
            ["$logical_path"] = RequireString(root, "logical_path"),
            ["$from_asset_key"] = RequireString(root, "from_asset_key"),
            ["$from_name"] = RequireString(root, "from_name"),
            ["$from_type_name"] = RequireString(root, "from_type_name"),
            ["$from_path_id"] = RequireInt64(root, "from_path_id"),
            ["$to_asset_key"] = RequireString(root, "to_asset_key"),
            ["$to_name"] = RequireString(root, "to_name"),
            ["$to_type_name"] = RequireString(root, "to_type_name"),
            ["$to_path_id"] = RequireInt64(root, "to_path_id"),
            ["$to_file_id"] = RequireInt32(root, "to_file_id"),
            ["$reference_kind"] = RequireString(root, "reference_kind"),
        });
    }

    private static void InsertString(SqliteConnection connection, SqliteTransaction transaction, JsonElement root)
    {
        InsertString(
            connection,
            transaction,
            RequireString(root, "job_id"),
            RequireString(root, "logical_path"),
            RequireInt64(root, "offset"),
            RequireString(root, "value"));
    }

    private static void InsertString(SqliteConnection connection, SqliteTransaction transaction, string jobId, string logicalPath, long offset, string value)
    {
        ExecuteNonQuery(connection, """
            INSERT OR IGNORE INTO strings (job_id, logical_path, offset, value)
            VALUES ($job_id, $logical_path, $offset, $value)
            """, transaction, new Dictionary<string, object?>
        {
            ["$job_id"] = jobId,
            ["$logical_path"] = logicalPath,
            ["$offset"] = offset,
            ["$value"] = value,
        });
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        if (parameters is not null)
        {
            foreach ((string key, object? value) in parameters)
            {
                command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<(long Offset, string Value)> ExtractUsefulStrings(byte[] data)
    {
        List<(long Offset, string Value)> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((long offset, string value) in ExtractAsciiStrings(data).Concat(ExtractUtf16LeStrings(data)))
        {
            if (value.Length is < 4 or > 260)
            {
                continue;
            }
            if (!UsefulStringRegex.IsMatch(value))
            {
                continue;
            }
            if (seen.Add(value))
            {
                result.Add((offset, value));
            }
        }

        return result;
    }

    private static IReadOnlyList<(long Offset, string Value)> ExtractAsciiStrings(byte[] data)
    {
        List<(long Offset, string Value)> result = new();
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] is >= 0x20 and <= 0x7E)
            {
                if (start < 0)
                {
                    start = i;
                }
                continue;
            }

            if (start >= 0 && i - start >= 4)
            {
                result.Add((start, Encoding.ASCII.GetString(data[start..i])));
            }
            start = -1;
        }

        if (start >= 0 && data.Length - start >= 4)
        {
            result.Add((start, Encoding.ASCII.GetString(data[start..])));
        }

        return result;
    }

    private static IReadOnlyList<(long Offset, string Value)> ExtractUtf16LeStrings(byte[] data)
    {
        List<(long Offset, string Value)> result = new();
        for (int parity = 0; parity < 2; parity++)
        {
            int start = -1;
            StringBuilder builder = new();
            for (int i = parity; i + 1 < data.Length; i += 2)
            {
                ushort value = (ushort)(data[i] | (data[i + 1] << 8));
                if (value is >= 0x20 and <= 0x7E)
                {
                    if (start < 0)
                    {
                        start = i;
                    }
                    builder.Append((char)value);
                    continue;
                }

                Flush();
            }

            Flush();

            void Flush()
            {
                if (start >= 0 && builder.Length >= 4)
                {
                    result.Add((start, builder.ToString()));
                }
                start = -1;
                builder.Clear();
            }
        }

        return result;
    }

    private static string BuildAssetKey(string logicalPath, long pathId)
    {
        return logicalPath + "#" + pathId.ToString("X");
    }

    private static string BuildStableQueryJobId(string logicalPath)
    {
        return "query-ab-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logicalPath))).Substring(0, 16).ToLowerInvariant();
    }

    private static string BuildInputHash(LogicalFileRecord job)
    {
        string value = string.Join("|", job.LogicalPath, job.ChunkPath, job.Offset, job.Length, job.FileDataMd5, job.FileChunkMd5Name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string GetClassIdName(int classId, string fallback)
    {
        return Enum.IsDefined(typeof(ClassIDType), classId) ? ((ClassIDType)classId).ToString() : fallback;
    }

    private static string GetJobOutputPath(string outputsDirectory, string jobId) => Path.Combine(outputsDirectory, jobId + ".jsonl");

    private static string GetJobLogPath(string logsDirectory, string jobId) => Path.Combine(logsDirectory, jobId + ".log");

    private static string SanitizeFileName(string name)
    {
        StringBuilder builder = new(name.Length == 0 ? 8 : name.Length);
        foreach (char c in string.IsNullOrWhiteSpace(name) ? "asset.ab" : name)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }
        return builder.ToString();
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"missing required string property '{propertyName}'");
        }
        return value.GetString() ?? string.Empty;
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    }

    private static long RequireInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"missing required integer property '{propertyName}'");
        }
        return value.GetInt64();
    }

    private static int RequireInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"missing required integer property '{propertyName}'");
        }
        return value.GetInt32();
    }

    private static IEnumerable<(string SourceRoot, string VfsRoot)> EnumerateVfsRoots(string dataDirectory)
    {
        string streaming = Path.Combine(dataDirectory, "StreamingAssets", "VFS");
        if (Directory.Exists(streaming))
        {
            yield return ("StreamingAssets", streaming);
        }

        string persistent = Path.Combine(dataDirectory, "Persistent", "VFS");
        if (Directory.Exists(persistent))
        {
            yield return ("Persistent", persistent);
        }
    }

    private static string ResolveDataDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(Path.Combine(fullPath, "StreamingAssets")))
        {
            return fullPath;
        }

        foreach (string candidate in Directory.EnumerateDirectories(fullPath, "*_Data", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(Path.Combine(candidate, "StreamingAssets")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Cannot locate Endfield data directory from: {path}");
    }

    private sealed record ReferenceRecord(
        string FromAssetKey,
        string FromName,
        string FromTypeName,
        long FromPathId,
        string ToAssetKey,
        string ToName,
        string ToTypeName,
        long ToPathId,
        int ToFileId,
        string ReferenceKind);

    private static class ReferenceExtractor
    {
        private static readonly HashSet<string> ExcludedProperties = new(StringComparer.Ordinal)
        {
            nameof(IUnityObjectBase.AssetInfo),
            nameof(IUnityObjectBase.Collection),
            nameof(IUnityObjectBase.MainAsset),
            nameof(IUnityObjectBase.OriginalPath),
            nameof(IUnityObjectBase.OriginalDirectory),
            nameof(IUnityObjectBase.OriginalName),
            nameof(IUnityObjectBase.OriginalExtension),
            nameof(IUnityObjectBase.OverridePath),
            nameof(IUnityObjectBase.OverrideDirectory),
            nameof(IUnityObjectBase.OverrideName),
            nameof(IUnityObjectBase.OverrideExtension),
            nameof(IUnityObjectBase.AssetBundleName),
        };

        public static IReadOnlyList<ReferenceRecord> Extract(IUnityObjectBase asset, string assetKey, string logicalPath, string cab)
        {
            List<ReferenceRecord> result = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            Visit(asset, asset, assetKey, logicalPath, cab, asset.GetType().Name, 0, result, seen);
            return result;
        }

        private static void Visit(
            IUnityObjectBase source,
            object? value,
            string sourceAssetKey,
            string logicalPath,
            string cab,
            string path,
            int depth,
            List<ReferenceRecord> result,
            HashSet<string> seen)
        {
            if (value is null || result.Count >= MaxReferencesPerAsset || depth > MaxTraversalDepth)
            {
                return;
            }

            if (TryReadPPtr(value, out int fileId, out long pathId))
            {
                if (pathId == 0)
                {
                    return;
                }

                IUnityObjectBase? target = source.Collection.TryGetAsset(fileId, pathId);
                string targetKey = target is null ? $"{cab}@file{fileId}#{pathId:X}" : BuildAssetKey(logicalPath, target.PathID);
                string unique = sourceAssetKey + ">" + targetKey + "@" + path;
                if (seen.Add(unique))
                {
                    result.Add(new ReferenceRecord(
                        sourceAssetKey,
                        source.GetBestName(),
                        GetClassIdName(source.ClassID, source.ClassName),
                        source.PathID,
                        targetKey,
                        target?.GetBestName() ?? string.Empty,
                        target is null ? string.Empty : GetClassIdName(target.ClassID, target.ClassName),
                        pathId,
                        fileId,
                        path));
                }
                return;
            }

            Type type = value.GetType();
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
            {
                return;
            }

            if (value is IUnityObjectBase && !ReferenceEquals(value, source))
            {
                return;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                int index = 0;
                foreach (object? item in enumerable)
                {
                    if (index >= MaxEnumerableItems)
                    {
                        break;
                    }
                    Visit(source, item, sourceAssetKey, logicalPath, cab, $"{path}[{index}]", depth + 1, result, seen);
                    index++;
                }
                return;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 || ExcludedProperties.Contains(property.Name))
                {
                    continue;
                }
                if (property.PropertyType == typeof(string) || property.PropertyType.IsPrimitive || property.PropertyType.IsEnum)
                {
                    continue;
                }

                object? child;
                try
                {
                    child = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                Visit(source, child, sourceAssetKey, logicalPath, cab, path + "." + property.Name, depth + 1, result, seen);
            }
        }

        private static bool TryReadPPtr(object value, out int fileId, out long pathId)
        {
            if (value is IPPtr pptr)
            {
                fileId = pptr.FileID;
                pathId = pptr.PathID;
                return true;
            }

            Type type = value.GetType();
            if (!type.Name.StartsWith("PPtr", StringComparison.Ordinal))
            {
                fileId = 0;
                pathId = 0;
                return false;
            }

            PropertyInfo? fileIdProperty = type.GetProperty("FileID", BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo? pathIdProperty = type.GetProperty("PathID", BindingFlags.Instance | BindingFlags.Public);
            if (fileIdProperty is null || pathIdProperty is null)
            {
                fileId = 0;
                pathId = 0;
                return false;
            }

            try
            {
                fileId = Convert.ToInt32(fileIdProperty.GetValue(value));
                pathId = Convert.ToInt64(pathIdProperty.GetValue(value));
                return true;
            }
            catch
            {
                fileId = 0;
                pathId = 0;
                return false;
            }
        }
    }
}
