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
    public string? EndFieldNativeReaderProbePath { get; init; }
    public string? EndFieldAssetLocatorPath { get; init; }
    public string? EndFieldQuery { get; init; }
    public int EndFieldLocatorMaxMatches { get; init; } = 32;
    public string? EndFieldMetadataOut { get; init; }
    public string? EndFieldVfsIndexPath { get; init; }
    public string? EndFieldVfsIndexOut { get; init; }
    public string? EndFieldBuildAssetIndexPath { get; init; }
    public string? EndFieldQueryIndexPath { get; init; }
    public string? EndFieldIndexDbPath { get; init; }
    public int EndFieldIndexParallel { get; init; } = 1;
    public bool EndFieldDeepAssetIndex { get; init; }
    public string[] EndFieldTargetTypes { get; init; } = [];
    public string? EndFieldReportOut { get; init; }

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

    /// <summary>
    /// With <see cref="CabMapPath"/>, load ONLY the bundles that contain an asset of one of these
    /// ClassID names (plus their transitive dependencies), instead of the whole game. The
    /// "build map then precisely filter" path — e.g. <c>--load-types Shader ComputeShader</c> to
    /// export shaders without loading every chk into memory. May be used without <c>--load</c>.
    /// </summary>
    public string[] LoadTypes { get; init; } = [];
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
    public Option<string?> EndFieldNativeReaderProbe { get; }
    public Option<string?> EndFieldAssetLocator { get; }
    public Option<string?> EndFieldQuery { get; }
    public Option<int> EndFieldLocatorMaxMatches { get; }
    public Option<string?> EndFieldMetadataOut { get; }
    public Option<string?> EndFieldVfsIndex { get; }
    public Option<string?> EndFieldVfsIndexOut { get; }
    public Option<string?> EndFieldBuildAssetIndex { get; }
    public Option<string?> EndFieldQueryIndex { get; }
    public Option<string?> EndFieldIndexDb { get; }
    public Option<int> EndFieldIndexParallel { get; }
    public Option<bool> EndFieldDeepAssetIndex { get; }
    public Option<string[]> EndFieldTargetTypes { get; }
    public Option<string?> EndFieldReportOut { get; }
    public Option<string?> BuildCabMap { get; }
    public Option<string?> CabMap { get; }
    public Option<string[]> LoadTypes { get; }
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
        EndFieldNativeReaderProbe = new Option<string?>("--endfield-native-reader-probe", "Run offline EndField native reader probe for an Endfield_Data directory and exit.");
        EndFieldAssetLocator = new Option<string?>("--endfield-asset-locator", "Run offline EndField VFS asset/string locator for an Endfield_Data directory and exit.");
        EndFieldQuery = new Option<string?>("--query", "Query string for offline locator probes.");
        EndFieldLocatorMaxMatches = new Option<int>("--locator-max-matches", () => 32, "Maximum locator matches to print before stopping.");
        EndFieldMetadataOut = new Option<string?>("--metadata-out", "Write aggregated metadata JSON for --endfield-asset-locator query.");
        EndFieldVfsIndex = new Option<string?>("--endfield-vfs-index", "Write EndField VFS logical file indexes for a game root or Endfield_Data directory and exit.");
        EndFieldVfsIndexOut = new Option<string?>("--index-out", "Output root for --endfield-vfs-index (default: ./out/endfield_vfs_index).");
        EndFieldBuildAssetIndex = new Option<string?>("--endfield-build-asset-index", "Build an offline EndField SQLite asset index for a game root or Endfield_Data directory and exit.");
        EndFieldQueryIndex = new Option<string?>("--endfield-query-index", "Query an offline EndField SQLite asset index and exit.");
        EndFieldIndexDb = new Option<string?>("--index-db", "SQLite database path for EndField asset index build/query.");
        EndFieldIndexParallel = new Option<int>("--parallel", () => 1, "Requested worker count for EndField asset indexing. The DB writer remains single-owner.");
        EndFieldDeepAssetIndex = new Option<bool>("--deep-asset-index", "During --endfield-build-asset-index, fully parse every logical .ab with AssetRipper instead of building the fast VFS/string index.");
        EndFieldTargetTypes = new Option<string[]>("--target-types", "Target ClassID names for index query filtering (default: Material Texture2D Mesh).")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        EndFieldReportOut = new Option<string?>("--report-out", "Write EndField index query report JSON.");
        BuildCabMap = new Option<string?>("--build-cab-map", "Build a CABMap (.bin) for --load[0] and exit. Format matches the GUI Asset Browser CABMap.");
        CabMap = new Option<string?>("--cab-map", "Load a CABMap (.bin) and expand each --load entry to its transitive CAB dependencies before handing files to AR.");
        LoadTypes = new Option<string[]>("--load-types", "With --cab-map, load only bundles containing these ClassID names (+ deps), e.g. Shader ComputeShader. Build the map first with --build-cab-map.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
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
            EndFieldNativeReaderProbe,
            EndFieldAssetLocator,
            EndFieldQuery,
            EndFieldLocatorMaxMatches,
            EndFieldMetadataOut,
            EndFieldVfsIndex,
            EndFieldVfsIndexOut,
            EndFieldBuildAssetIndex,
            EndFieldQueryIndex,
            EndFieldIndexDb,
            EndFieldIndexParallel,
            EndFieldDeepAssetIndex,
            EndFieldTargetTypes,
            EndFieldReportOut,
            BuildCabMap,
            CabMap,
            LoadTypes,
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
            EndFieldNativeReaderProbePath = pr.GetValueForOption(EndFieldNativeReaderProbe),
            EndFieldAssetLocatorPath = pr.GetValueForOption(EndFieldAssetLocator),
            EndFieldQuery = pr.GetValueForOption(EndFieldQuery),
            EndFieldLocatorMaxMatches = pr.GetValueForOption(EndFieldLocatorMaxMatches),
            EndFieldMetadataOut = pr.GetValueForOption(EndFieldMetadataOut),
            EndFieldVfsIndexPath = pr.GetValueForOption(EndFieldVfsIndex),
            EndFieldVfsIndexOut = pr.GetValueForOption(EndFieldVfsIndexOut),
            EndFieldBuildAssetIndexPath = pr.GetValueForOption(EndFieldBuildAssetIndex),
            EndFieldQueryIndexPath = pr.GetValueForOption(EndFieldQueryIndex),
            EndFieldIndexDbPath = pr.GetValueForOption(EndFieldIndexDb),
            EndFieldIndexParallel = pr.GetValueForOption(EndFieldIndexParallel),
            EndFieldDeepAssetIndex = pr.GetValueForOption(EndFieldDeepAssetIndex),
            EndFieldTargetTypes = pr.GetValueForOption(EndFieldTargetTypes) ?? [],
            EndFieldReportOut = pr.GetValueForOption(EndFieldReportOut),
            BuildCabMapPath = pr.GetValueForOption(BuildCabMap),
            CabMapPath = pr.GetValueForOption(CabMap),
            LoadTypes = pr.GetValueForOption(LoadTypes) ?? [],
            Passthrough = pr.GetValueForArgument(Passthrough) ?? [],
        };
    }
}
