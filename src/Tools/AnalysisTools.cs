using System.ComponentModel;
using System.Text.Json;
using ETLReader.Analysis;
using ETLReader.Session;
using ModelContextProtocol.Server;

namespace ETLReader.Tools;

public class AnalysisTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly TraceSession _session;

    public AnalysisTools(TraceSession session) => _session = session;

    [McpServerTool(Name = "list_processes")]
    [Description("List all processes in the trace with CPU time. Use this to discover process names for filtering.")]
    public string ListProcesses(
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null,
        [Description("Max results (default: 30)")] int take = 30)
    {
        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        var processes = analyzer.GetProcesses().Take(take).ToList();
        return Json(processes);
    }

    [McpServerTool(Name = "get_cpu_stacks")]
    [Description("Analyze WHERE CPU time was spent using sampled call stacks. mode='tree' for top frames by name, 'hotpath' for the single deepest hot path, 'diff' to compare baseline vs target.")]
    public string GetCpuStacks(
        [Description("'tree' (default), 'hotpath', or 'diff'")] string mode = "tree",
        [Description("Filter by process name")] string? processName = null,
        [Description("Filter by process ID")] int? processId = null,
        [Description("Start of time range (ms)")] double? startMs = null,
        [Description("End of time range (ms)")] double? endMs = null,
        [Description("Min inclusive % of total CPU (default: 1.0)")] double minPercent = 1.0,
        [Description("Max results (default: 50)")] int take = 50,
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        if (mode == "diff")
        {
            if (!_session.IsComparison)
                return Json(new { error = "Diff mode requires both baseline and target loaded." });

            var target = GetAnalyzer("target");
            var baseline = GetAnalyzer("baseline");
            if (target == null || baseline == null) return NotReady();

            try
            {
                var diff = target.DiffCpuStacks(baseline, startMs, endMs, processName, processId, minPercent);
                if (diff.Count == 0)
                    return Json(new { data = Array.Empty<object>(), note = "No CPU sample differences found. The trace may lack PerfInfo/Sample events, or the time range/process filter matched no samples." });
                return Json(diff);
            }
            catch (Exception ex)
            {
                return Json(new { error = $"CPU stack diff failed: {ex.Message}", hint = "Ensure both traces contain CPU sampling (PerfInfo) events for the specified process." });
            }
        }

        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        try
        {
            if (mode == "hotpath")
            {
                var path = analyzer.GetHotPath(startMs, endMs, processName, processId);
                if (path.Count == 0)
                    return Json(new { data = Array.Empty<object>(), note = "No hot path found. The trace may lack CPU sampling events for this process/time range." });
                return Json(path);
            }

            var frames = analyzer.GetCpuStacks(startMs, endMs, processName, processId, minPercent, take);
            if (frames.Count == 0)
                return Json(new { data = Array.Empty<object>(), note = "No CPU samples found. The trace may lack PerfInfo/Sample events, or the process/time filter matched nothing." });
            return Json(frames);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"CPU stack analysis failed: {ex.Message}", hint = "This usually means the trace lacks CPU sampling events (PerfInfo provider)." });
        }
    }

    [McpServerTool(Name = "get_clr_data")]
    [Description("Analyze .NET CLR runtime behavior. category='allocations', 'jit', 'exceptions', 'contention', or 'gc'. Requires CLR ETW provider to have been enabled during trace collection.")]
    public string GetClrData(
        [Description("One of: 'allocations', 'jit', 'exceptions', 'contention', 'gc'")] string category,
        [Description("Filter by process name")] string? processName = null,
        [Description("Filter by process ID")] int? processId = null,
        [Description("Start of time range (ms)")] double? startMs = null,
        [Description("End of time range (ms)")] double? endMs = null,
        [Description("Sort field (category-dependent)")] string? sortBy = null,
        [Description("'assembly' or 'method' (jit only, default: 'assembly')")] string groupBy = "assembly",
        [Description("Max results (default: 50)")] int take = 50,
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        try
        {
            object result = category switch
            {
                "allocations" => analyzer.GetAllocations(startMs, endMs, processName, processId, sortBy ?? "bytes", take),
                "jit" => analyzer.GetJitEvents(startMs, endMs, processName, processId, groupBy, sortBy ?? "size", take),
                "exceptions" => analyzer.GetExceptions(startMs, endMs, processName, processId, take),
                "contention" => analyzer.GetContention(startMs, endMs, processName, processId, take),
                "gc" => analyzer.GetGcEvents(startMs, endMs, processName, processId, take),
                _ => (object)new { error = $"Unknown category '{category}'. Use: allocations, jit, exceptions, contention, gc." }
            };

            // Check for empty results and explain why
            if (result is System.Collections.ICollection { Count: 0 })
                return Json(new { data = Array.Empty<object>(), note = $"No {category} data found. The CLR ETW provider may not have been enabled during trace collection, or no {category} events occurred for the specified process/time range." });

            return Json(result);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"CLR {category} analysis failed: {ex.Message}", hint = "Ensure the trace was collected with the CLR ETW provider enabled." });
        }
    }

    [McpServerTool(Name = "get_io_activity")]
    [Description("Analyze file I/O — reads and writes aggregated by file path. Requires FileIO kernel provider in the trace.")]
    public string GetIoActivity(
        [Description("Filter by process name")] string? processName = null,
        [Description("Filter by process ID")] int? processId = null,
        [Description("Start of time range (ms)")] double? startMs = null,
        [Description("End of time range (ms)")] double? endMs = null,
        [Description("'read', 'write', or 'all' (default: 'all')")] string ioType = "all",
        [Description("Regex filter on file path")] string? filter = null,
        [Description("Max results (default: 50)")] int take = 50,
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        try
        {
            var results = analyzer.GetFileIo(startMs, endMs, processName, processId, ioType, take);

            if (filter != null)
            {
                var regex = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                results = results.Where(r => regex.IsMatch(r.FilePath)).ToList();
            }

            if (results.Count == 0)
                return Json(new { data = Array.Empty<object>(), note = "No file I/O events found. The FileIO kernel provider may not have been enabled during trace collection, or no I/O occurred for the specified filters." });

            return Json(results);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"File I/O analysis failed: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Get process memory usage snapshots. Reads ProcessCounters ETW events if present; not all traces contain this data.")]
    public string GetMemory(
        [Description("Filter by process name")] string? processName = null,
        [Description("Filter by process ID")] int? processId = null,
        [Description("Max results (default: 30)")] int take = 30,
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        try
        {
            var snapshots = analyzer.GetMemorySnapshots(processName, processId).Take(take).ToList();
            if (snapshots.Count == 0)
                return Json(new { data = Array.Empty<object>(), note = "No memory data found. The trace does not contain ProcessCounters or VirtualAlloc events. Memory snapshots require specific ETW providers to be enabled during collection." });
            return Json(snapshots);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Memory analysis failed: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "get_etw_events")]
    [Description("Query ETW events by provider. Omit providerName to list all providers with event counts.")]
    public string GetEtwEvents(
        [Description("Provider name. Omit to list all providers.")] string? providerName = null,
        [Description("Filter to specific event name")] string? eventName = null,
        [Description("Filter by process name")] string? processName = null,
        [Description("Filter by process ID")] int? processId = null,
        [Description("Start of time range (ms)")] double? startMs = null,
        [Description("End of time range (ms)")] double? endMs = null,
        [Description("Pagination offset (default: 0)")] int skip = 0,
        [Description("Page size (default: 100)")] int take = 100,
        [Description("'baseline' or 'target' (default: 'target')")] string? side = null)
    {
        var analyzer = GetAnalyzer(side);
        if (analyzer == null) return NotReady();

        try
        {
            if (providerName == null)
            {
                var providers = analyzer.GetProviders(processName, processId);
                if (providers.Count == 0)
                    return Json(new { data = Array.Empty<object>(), note = "No ETW providers found for the specified process filter." });
                return Json(providers);
            }

            var events = analyzer.GetEvents(providerName, eventName, startMs, endMs, processName, processId, skip, take);
            if (events.Count == 0)
                return Json(new { data = Array.Empty<object>(), note = $"No events found for provider '{providerName}' with the specified filters." });
            return Json(events);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"ETW event query failed: {ex.Message}" });
        }
    }

    private EtlAnalyzer? GetAnalyzer(string? side)
    {
        if (!_session.IsLoaded) return null;
        var pkg = _session.GetSide(side);
        return pkg.Analyzer;
    }

    private static string NotReady() => Json(new
    {
        error = "ETL not ready. Call prepare_etl(step='extract') then prepare_etl(step='index') first."
    });

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOpts);
}
