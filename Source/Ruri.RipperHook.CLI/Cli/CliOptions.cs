using System.CommandLine;
using System.CommandLine.Binding;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.CLI;

internal sealed class CliOptions
{
    public string[] Hooks { get; init; } = [];
    public string[] LoadPaths { get; init; } = [];
    public string? ExportPath { get; init; }
    public bool ListHooks { get; init; }
    public string[] Types { get; init; } = [];
    public Regex[] Names { get; init; } = [];
    public int SmokeTestLimit { get; init; }
    public bool Silent { get; init; }
    public LogType LogLevel { get; init; } = LogType.Info;
    public bool FailFast { get; init; } = true;
    public string[] Passthrough { get; init; } = [];

    /// <summary>
    /// Set to write a CABMap (.bin) for the directory given in <see cref="LoadPaths"/>[0],
    /// then exit. Scanning every file in a big game tree is slow (minutes for Endfield_Data),
    /// so build once and reuse via <see cref="CabMapPath"/>.
    /// </summary>
    public string? BuildCabMapPath { get; init; }

    /// <summary>
    /// When set, the CABMap at this path is loaded and used to resolve the transitive
    /// dependency closure of each file in <see cref="LoadPaths"/>. AR then sees every chk the
    /// seed bundles cross-reference, which is the only way to get a complete character
    /// AnimatorController (its BlendTrees / clip refs live in sibling chks).
    /// </summary>
    public string? CabMapPath { get; init; }

    public string? GameRoot { get; init; }
    public string? OutputRoot { get; init; }
    public string? VfsDbPath { get; init; }
    public bool BuildVfsIndex { get; init; }
    public bool ProbeVfsMetadata { get; init; }
    public bool ProbeVfsHitMetadata { get; init; }
    public bool Resume { get; init; }
    public string? Shard { get; init; }
    public string? ScanVfsTerms { get; init; }
    public string? VfsDeps { get; init; }
    public string? ClosureOut { get; init; }
    public string? LoadLogical { get; init; }
    public bool ResolveVfsDeps { get; init; }
    public string? RepairUnityMaterials { get; init; }
    public string? RepairReport { get; init; }
}

internal sealed class CliOptionsBinder : BinderBase<CliOptions>
{
    public Option<string[]> Hook { get; }
    public Option<string[]> Load { get; }
    public Option<string?> Export { get; }
    public Option<bool> ListHooks { get; }
    public Option<string[]> Types { get; }
    public Option<Regex[]> Names { get; }
    public Option<int> SmokeTestLimit { get; }
    public Option<bool> Silent { get; }
    public Option<LogType> LogLevel { get; }
    public Option<bool> FailFast { get; }
    public Option<string?> BuildCabMap { get; }
    public Option<string?> CabMap { get; }
    public Option<string?> GameRoot { get; }
    public Option<string?> OutputRoot { get; }
    public Option<string?> VfsDb { get; }
    public Option<bool> BuildVfsIndex { get; }
    public Option<bool> ProbeVfsMetadata { get; }
    public Option<bool> ProbeVfsHitMetadata { get; }
    public Option<bool> Resume { get; }
    public Option<string?> Shard { get; }
    public Option<string?> ScanVfsTerms { get; }
    public Option<string?> VfsDeps { get; }
    public Option<string?> ClosureOut { get; }
    public Option<string?> LoadLogical { get; }
    public Option<bool> ResolveVfsDeps { get; }
    public Option<string?> RepairUnityMaterials { get; }
    public Option<string?> RepairReport { get; }
    public Argument<string[]> Passthrough { get; }

    public CliOptionsBinder()
    {
        Hook = new Option<string[]>("--hook", "Enable hook by id (GameName_Version). Repeatable.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Load = new Option<string[]>("--load", "Files or directories to load (repeatable). Triggers headless export when set.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Export = new Option<string?>("--export", "Export directory.");
        ListHooks = new Option<bool>("--list-hooks", "List every available hook id and exit (code 3).");
        Types = new Option<string[]>("--types", "Filter to these ClassID names (repeatable; e.g. Shader Texture2D).")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Names = new Option<Regex[]>(
            "--names",
            parseArgument: result =>
            {
                List<Regex> items = new();
                if (result.Tokens.Count == 1 && File.Exists(result.Tokens[0].Value))
                {
                    foreach (string line in File.ReadLines(result.Tokens[0].Value))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { items.Add(new Regex(line, RegexOptions.IgnoreCase)); }
                        catch (ArgumentException) { }
                    }
                }
                else
                {
                    foreach (var token in result.Tokens)
                    {
                        try { items.Add(new Regex(token.Value, RegexOptions.IgnoreCase)); }
                        catch (ArgumentException) { }
                    }
                }
                return items.ToArray();
            },
            isDefault: false,
            description: "Asset name regex filter(s). Single token that is an existing file path is loaded line-by-line.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        SmokeTestLimit = new Option<int>("--smoke-test-limit", () => 0, "Limit export to N assets per matching ClassID (0 = unlimited).");
        Silent = new Option<bool>("--silent", "Suppress non-error log output.");
        LogLevel = new Option<LogType>("--log-level", () => LogType.Info, "Log level threshold (Verbose|Debug|Info|Warning|Error).");
        FailFast = new Option<bool>("--fail-fast", () => true, "Abort on first per-asset export failure (default true).");
        BuildCabMap = new Option<string?>("--build-cab-map", "Build a CABMap (.bin) for --load[0] and exit. Format matches the GUI Asset Browser CABMap.");
        CabMap = new Option<string?>("--cab-map", "Load a CABMap (.bin) and expand each --load entry to its transitive CAB dependencies before handing files to AR.");
        GameRoot = new Option<string?>("--game-root", "Endfield game root for VFS indexing.");
        OutputRoot = new Option<string?>("--output-root", "Directory for VFS reports and temporary materialized bundles.");
        VfsDb = new Option<string?>("--vfs-db", "SQLite database for Endfield VFS logical .ab / CAB metadata.");
        BuildVfsIndex = new Option<bool>("--build-vfs-index", "Build or refresh the Endfield VFS logical file index.");
        ProbeVfsMetadata = new Option<bool>("--probe-vfs-metadata", "Probe indexed VFS .ab payloads and record CAB dependencies.");
        ProbeVfsHitMetadata = new Option<bool>("--probe-vfs-hit-metadata", "When probing VFS metadata, only process payloads present in term_hits.");
        Resume = new Option<bool>("--resume", "Skip VFS metadata payloads that already have terminal status.");
        Shard = new Option<string?>("--shard", "Process shard i/n for long VFS metadata or term scans.");
        ScanVfsTerms = new Option<string?>("--scan-vfs-terms", "Scan indexed logical .ab payloads for terms. Value is a file path or comma-separated terms.");
        VfsDeps = new Option<string?>("--vfs-deps", "Resolve transitive VFS CAB dependencies from a logical .ab path or CAB name.");
        ClosureOut = new Option<string?>("--closure-out", "Write VFS dependency closure or scan report JSON to this path.");
        LoadLogical = new Option<string?>("--load-logical", "Materialize and load one indexed logical .ab path or CAB seed.");
        ResolveVfsDeps = new Option<bool>("--resolve-vfs-deps", "Expand --load-logical through the VFS CAB dependency closure before loading.");
        RepairUnityMaterials = new Option<string?>("--repair-unity-materials", "Inspect one ExportedProject and write material dependency reports.");
        RepairReport = new Option<string?>("--repair-report", "Directory for material repair/dependency reports.");
        Passthrough = new Argument<string[]>("passthrough", () => [], "Forwarded to AssetRipper Web UI when --load is omitted.");
        Passthrough.Arity = ArgumentArity.ZeroOrMore;
    }

    public RootCommand BuildRoot()
    {
        var root = new RootCommand("Ruri.RipperHook CLI")
        {
            Hook,
            Load,
            Export,
            ListHooks,
            Types,
            Names,
            SmokeTestLimit,
            Silent,
            LogLevel,
            FailFast,
            BuildCabMap,
            CabMap,
            GameRoot,
            OutputRoot,
            VfsDb,
            BuildVfsIndex,
            ProbeVfsMetadata,
            ProbeVfsHitMetadata,
            Resume,
            Shard,
            ScanVfsTerms,
            VfsDeps,
            ClosureOut,
            LoadLogical,
            ResolveVfsDeps,
            RepairUnityMaterials,
            RepairReport,
            Passthrough,
        };
        return root;
    }

    protected override CliOptions GetBoundValue(BindingContext bindingContext)
    {
        var pr = bindingContext.ParseResult;
        return new CliOptions
        {
            Hooks = pr.GetValueForOption(Hook) ?? [],
            LoadPaths = pr.GetValueForOption(Load) ?? [],
            ExportPath = pr.GetValueForOption(Export),
            ListHooks = pr.GetValueForOption(ListHooks),
            Types = pr.GetValueForOption(Types) ?? [],
            Names = pr.GetValueForOption(Names) ?? [],
            SmokeTestLimit = pr.GetValueForOption(SmokeTestLimit),
            Silent = pr.GetValueForOption(Silent),
            LogLevel = pr.GetValueForOption(LogLevel),
            FailFast = pr.GetValueForOption(FailFast),
            BuildCabMapPath = pr.GetValueForOption(BuildCabMap),
            CabMapPath = pr.GetValueForOption(CabMap),
            GameRoot = pr.GetValueForOption(GameRoot),
            OutputRoot = pr.GetValueForOption(OutputRoot),
            VfsDbPath = pr.GetValueForOption(VfsDb),
            BuildVfsIndex = pr.GetValueForOption(BuildVfsIndex),
            ProbeVfsMetadata = pr.GetValueForOption(ProbeVfsMetadata),
            ProbeVfsHitMetadata = pr.GetValueForOption(ProbeVfsHitMetadata),
            Resume = pr.GetValueForOption(Resume),
            Shard = pr.GetValueForOption(Shard),
            ScanVfsTerms = pr.GetValueForOption(ScanVfsTerms),
            VfsDeps = pr.GetValueForOption(VfsDeps),
            ClosureOut = pr.GetValueForOption(ClosureOut),
            LoadLogical = pr.GetValueForOption(LoadLogical),
            ResolveVfsDeps = pr.GetValueForOption(ResolveVfsDeps),
            RepairUnityMaterials = pr.GetValueForOption(RepairUnityMaterials),
            RepairReport = pr.GetValueForOption(RepairReport),
            Passthrough = pr.GetValueForArgument(Passthrough) ?? [],
        };
    }
}
