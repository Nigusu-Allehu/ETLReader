using System.ComponentModel;
using System.Text.Json;
using ETLReader.Session;
using ModelContextProtocol.Server;

namespace ETLReader.Tools;

public class SessionTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly TraceSession _session;

    public SessionTools(TraceSession session) => _session = session;

    [McpServerTool(Name = "load_traces")]
    [Description("Load trace data into the active session. Must be called before other tools. Accepts .etl.zip files, raw .etl files, or folders containing either.")]
    public string LoadTraces(
        [Description("Path to baseline .etl.zip file or folder")] string? baselinePath = null,
        [Description("Path to target .etl.zip file or folder")] string? targetPath = null,
        [Description("Additional symbol path for resolving method names (e.g. 'srv*C:\\symbols*https://msdl.microsoft.com/download/symbols')")] string? symbolPath = null)
    {
        if (baselinePath == null && targetPath == null)
            return Json(new { error = "At least one of baselinePath or targetPath must be provided." });

        try
        {
            _session.Load(baselinePath, targetPath, symbolPath);

            var result = new Dictionary<string, object>
            {
                ["sessionType"] = _session.IsComparison ? "comparison" : "single"
            };

            if (_session.Baseline != null)
                result["baseline"] = PackageSummary(_session.Baseline);
            if (_session.Target != null)
                result["target"] = PackageSummary(_session.Target);

            return Json(result);
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "session_status")]
    [Description("Show what traces are currently loaded in the active session.")]
    public string SessionStatus()
    {
        if (!_session.IsLoaded)
            return Json(new { error = "No active session. Call load_traces first." });

        var result = new Dictionary<string, object>
        {
            ["sessionType"] = _session.IsComparison ? "comparison" : "single"
        };

        if (_session.Baseline != null)
            result["baseline"] = PackageSummary(_session.Baseline);
        if (_session.Target != null)
            result["target"] = PackageSummary(_session.Target);

        return Json(result);
    }

    [McpServerTool(Name = "prepare_etl")]
    [Description("Prepare the raw ETL for analysis. step='extract' or 'index' starts work in the background. step='status' checks readiness. Call extract first, then index.")]
    public string PrepareEtl(
        [Description("'extract' (unzip ETL), 'index' (build ETLX + symbols), or 'status' (check readiness)")] string step = "status",
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        if (!_session.IsLoaded)
            return Json(new { error = "No active session. Call load_traces first." });

        try
        {
            var pkg = _session.GetSide(side);

            return step switch
            {
                "status" => HandleStatus(pkg),
                "extract" => HandleExtract(pkg),
                "index" => HandleIndex(pkg),
                _ => Json(new { error = $"Unknown step '{step}'. Use 'extract', 'index', or 'status'." })
            };
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    private string HandleStatus(TracePackage pkg)
    {
        return Json(new
        {
            extracting = pkg.IsExtracting,
            etlExtracted = pkg.IsEtlExtracted,
            extractError = pkg.ExtractError,
            indexing = pkg.IsIndexing,
            indexed = pkg.Analyzer != null,
            indexError = pkg.IndexError,
            progressPercent = pkg.ProgressPercent,
            ready = pkg.IsEtlExtracted && pkg.Analyzer != null,
            nextStep = pkg.IsExtracting ? "Extraction in progress — poll status again."
                : pkg.IsIndexing ? "Indexing in progress — poll status again."
                : !pkg.IsEtlExtracted ? "Call prepare_etl(step='extract')."
                : pkg.Analyzer == null ? "Call prepare_etl(step='index')."
                : "Ready for analysis."
        });
    }

    private string HandleExtract(TracePackage pkg)
    {
        if (pkg.IsEtlExtracted)
            return Json(new { status = "already_done", nextStep = pkg.Analyzer == null ? "Call prepare_etl(step='index')." : "Ready." });

        if (pkg.IsExtracting)
            return Json(new { status = "in_progress", message = "Already extracting — poll status." });

        pkg.IsExtracting = true;
        pkg.ExtractError = null;
        pkg.ProgressPercent = 0;
        _ = Task.Run(() =>
        {
            try { pkg.ExtractEtl(); }
            catch (Exception ex) { pkg.ExtractError = ex.Message; }
            finally { pkg.IsExtracting = false; }
        });

        return Json(new { status = "started", message = "Extraction started. Poll prepare_etl(step='status')." });
    }

    private string HandleIndex(TracePackage pkg)
    {
        if (!pkg.IsEtlExtracted)
            return Json(new { error = pkg.IsExtracting ? "Still extracting — wait for completion." : "ETL not extracted. Call prepare_etl(step='extract') first." });

        if (pkg.Analyzer != null)
            return Json(new { status = "already_done", message = "Index ready." });

        if (pkg.IsIndexing)
            return Json(new { status = "in_progress", message = "Already indexing — poll status." });

        pkg.IsIndexing = true;
        pkg.IndexError = null;
        pkg.ProgressPercent = 0;
        _ = Task.Run(() =>
        {
            try { pkg.Analyzer = new Analysis.EtlAnalyzer(pkg.EtlFilePath!, pkg.SymbolsPath, _session.SymbolPath, pct => pkg.ProgressPercent = pct); }
            catch (Exception ex) { pkg.IndexError = ex.Message; }
            finally { pkg.IsIndexing = false; }
        });

        return Json(new { status = "started", message = "Indexing started. Poll prepare_etl(step='status')." });
    }

    private static object PackageSummary(TracePackage pkg) => new
    {
        source = pkg.SourcePath,
        testName = pkg.TestName,
        traceType = pkg.TraceType.ToString(),
        iteration = pkg.Iteration,
        etlExtracted = pkg.IsEtlExtracted,
        indexed = pkg.Analyzer != null
    };

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOpts);
}
