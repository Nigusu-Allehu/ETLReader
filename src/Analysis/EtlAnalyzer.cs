using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Symbols;

namespace ETLReader.Analysis;

#region Records

public record ProcessInfo(string Name, int Id, double CpuMs, double StartMs, double EndMs, string CommandLine);
public record CpuFrame(string Name, double InclusivePercent, double ExclusivePercent, double InclusiveMs, double ExclusiveMs);
public record StackDiffFrame(string Name, double DeltaMs, double DeltaPercent);
public record FileIoSummary(string FilePath, string Process, int ProcessId, long ReadBytes, long WriteBytes, int ReadCount, int WriteCount);
public record AllocationInfo(string TypeName, long Bytes, int Count, string[] TopCallers);
public record JitInfo(string Name, long SizeBytes, int Count);
public record ExceptionInfo(string Type, int Count, string? Message);
public record ContentionInfo(string Frame, double DurationMs, int Count);
public record GcInfo(int Number, int Generation, double PauseMs, string Reason, long HeapSizeBytes, double TimestampMs);
public record MemorySnapshot(string ProcessName, int ProcessId, long WorkingSetBytes, long PrivateBytes, long VirtualBytes);
public record EtwEvent(double TimestampMs, string Provider, string Event, string Process, int ProcessId, Dictionary<string, string> Payload);
public record ProviderSummary(string Name, int EventCount);

#endregion

/// <summary>
/// Wraps TraceEvent/PerfView APIs to analyze raw ETL traces.
/// </summary>
public class EtlAnalyzer : IDisposable
{
    private readonly TraceLog _traceLog;
    private readonly SymbolReader _symbolReader;
    private List<ProviderSummary>? _cachedProviders;

    public EtlAnalyzer(string etlPath, string? bundledSymbolsPath = null, string? userSymbolPath = null)
    {
        // Use persistent ETLX cache
        var etlxCacheDir = Path.Combine(Path.GetTempPath(), "ETLReader", "etlx-cache");
        Directory.CreateDirectory(etlxCacheDir);
        var etlHash = $"{Path.GetFileName(etlPath)}_{new FileInfo(etlPath).Length}";
        var cachedEtlxPath = Path.Combine(etlxCacheDir, etlHash + ".etlx");

        if (File.Exists(cachedEtlxPath))
            _traceLog = new TraceLog(cachedEtlxPath);
        else
            _traceLog = new TraceLog(TraceLog.CreateFromEventTraceLogFile(etlPath, cachedEtlxPath));

        var symbolPath = BuildSymbolPath(bundledSymbolsPath, userSymbolPath);
        _symbolReader = new SymbolReader(TextWriter.Null, symbolPath);
        ResolveSymbols();
    }

    #region Process Discovery

    public List<ProcessInfo> GetProcesses()
    {
        var processes = new List<ProcessInfo>();
        foreach (var proc in _traceLog.Processes)
        {
            if (string.IsNullOrEmpty(proc.Name)) continue;
            var cpuMs = proc.CPUMSec;
            if (cpuMs < 1 && proc.Name != "Idle") continue;
            processes.Add(new ProcessInfo(
                proc.Name, proc.ProcessID,
                Math.Round(cpuMs, 1),
                Math.Round(proc.StartTimeRelativeMsec, 2),
                Math.Round(proc.EndTimeRelativeMsec, 2),
                proc.CommandLine ?? ""));
        }
        return processes.OrderByDescending(p => p.CpuMs).ToList();
    }

    #endregion

    #region CPU Stacks

    public List<CpuFrame> GetCpuStacks(double? startMs, double? stopMs, string? processName, int? processId, double minPercent, int take)
    {
        var stackSource = BuildCpuStackSource(startMs, stopMs, processName, processId);
        if (stackSource == null) return [];

        var callTree = new CallTree(ScalingPolicyKind.TimeMetric) { StackSource = stackSource };

        var frames = new List<CpuFrame>();
        foreach (var node in callTree.ByID)
        {
            if (node.InclusiveMetricPercent < minPercent) continue;
            frames.Add(new CpuFrame(
                node.Name,
                Math.Round(node.InclusiveMetricPercent, 1),
                Math.Round(node.ExclusiveMetricPercent, 1),
                Math.Round(node.InclusiveMetric, 1),
                Math.Round(node.ExclusiveMetric, 1)));
        }

        return frames.OrderByDescending(f => f.ExclusiveMs).Take(take).ToList();
    }

    public List<string> GetHotPath(double? startMs, double? stopMs, string? processName, int? processId)
    {
        var stackSource = BuildCpuStackSource(startMs, stopMs, processName, processId);
        if (stackSource == null) return [];

        var callTree = new CallTree(ScalingPolicyKind.TimeMetric) { StackSource = stackSource };

        var path = new List<string>();
        var node = callTree.Root;
        var totalMetric = Math.Abs(node.InclusiveMetric);
        if (totalMetric <= 0) return path;

        while (node.Callees != null && node.Callees.Count > 0 && path.Count < 30)
        {
            CallTreeNode? hottest = null;
            foreach (var child in node.Callees)
            {
                if (hottest == null || Math.Abs(child.InclusiveMetric) > Math.Abs(hottest.InclusiveMetric))
                    hottest = child;
            }
            if (hottest == null || Math.Abs(hottest.InclusiveMetric) <= 0) break;

            var pct = (Math.Abs(hottest.InclusiveMetric) / totalMetric) * 100;
            path.Add($"{hottest.Name} ({pct:F1}%, {Math.Abs(hottest.InclusiveMetric):F0}ms)");
            node = hottest;
        }

        return path;
    }

    public List<StackDiffFrame> DiffCpuStacks(EtlAnalyzer baseline, double? startMs, double? stopMs, string? processName, int? processId, double minPercent)
    {
        var targetSource = BuildCpuStackSource(startMs, stopMs, processName, processId);
        var baselineSource = baseline.BuildCpuStackSource(startMs, stopMs, processName, processId);
        if (targetSource == null || baselineSource == null) return [];

        var diffSource = InternStackSource.Diff(targetSource, baselineSource);
        var diffTree = new CallTree(ScalingPolicyKind.TimeMetric) { StackSource = diffSource };

        var results = new List<StackDiffFrame>();
        foreach (var node in diffTree.ByID)
        {
            if (Math.Abs(node.InclusiveMetricPercent) < minPercent) continue;
            results.Add(new StackDiffFrame(
                node.Name,
                Math.Round(node.InclusiveMetric, 1),
                Math.Round(node.InclusiveMetricPercent, 1)));
        }

        return results.OrderByDescending(f => Math.Abs(f.DeltaMs)).Take(50).ToList();
    }

    internal StackSource? BuildCpuStackSource(double? startMs, double? stopMs, string? processName, int? processId)
    {
        var events = _traceLog.Events
            .Filter(e => startMs == null || e.TimeStampRelativeMSec >= startMs.Value)
            .Filter(e => stopMs == null || e.TimeStampRelativeMSec <= stopMs.Value)
            .Filter(e => processName == null || (e.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) ?? false))
            .Filter(e => processId == null || e.ProcessID == processId.Value);

        var cpuSource = new TraceEventStackSource(events);

        try { cpuSource.LookupWarmSymbols(minCount: 5, _symbolReader, cpuSource, null); }
        catch { /* best effort */ }

        return cpuSource;
    }

    #endregion

    #region Allocations

    public List<AllocationInfo> GetAllocations(double? startMs, double? stopMs, string? processName, int? processId, string sortBy, int take)
    {
        var stackSource = new MutableTraceEventStackSource(_traceLog);
        var sample = new StackSourceSample(stackSource);
        var hasData = false;

        foreach (var ev in _traceLog.Events.ByEventType<GCAllocationTickTraceData>())
        {
            if (!InRange(ev, startMs, stopMs, processName, processId)) continue;

            var callStack = stackSource.GetCallStack(ev.CallStackIndex(), ev);
            var typeFrame = stackSource.Interner.FrameIntern("Type " + (ev.TypeName ?? "unknown"));
            var fullStack = stackSource.Interner.CallStackIntern(typeFrame, callStack);

            sample.StackIndex = fullStack;
            sample.Metric = ev.AllocationAmount64;
            sample.Count = 1;
            sample.TimeRelativeMSec = ev.TimeStampRelativeMSec;
            stackSource.AddSample(sample);
            hasData = true;
        }

        if (!hasData) return [];
        stackSource.DoneAddingSamples();

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = stackSource };

        var results = new List<AllocationInfo>();
        foreach (var node in callTree.ByID)
        {
            if (!node.Name.StartsWith("Type ")) continue;
            var typeName = node.Name["Type ".Length..];

            var topCallers = new List<string>();
            if (callTree.Root.Callees != null)
            {
                foreach (var rootChild in callTree.Root.Callees)
                {
                    if (rootChild.Name == node.Name && rootChild.Callees != null)
                    {
                        foreach (var caller in rootChild.Callees)
                        {
                            topCallers.Add(caller.Name);
                            if (topCallers.Count >= 3) break;
                        }
                        break;
                    }
                }
            }

            results.Add(new AllocationInfo(typeName, (long)node.InclusiveMetric, (int)node.InclusiveCount, topCallers.ToArray()));
        }

        var sorted = sortBy == "count"
            ? results.OrderByDescending(a => a.Count)
            : results.OrderByDescending(a => a.Bytes);

        return sorted.Take(take).ToList();
    }

    #endregion

    #region JIT

    public List<JitInfo> GetJitEvents(double? startMs, double? stopMs, string? processName, int? processId, string groupBy, string sortBy, int take)
    {
        var jitData = new Dictionary<string, (long sizeBytes, int count)>();

        foreach (var ev in _traceLog.Events.ByEventType<MethodLoadUnloadVerboseTraceData>())
        {
            if (!InRange(ev, startMs, stopMs, processName, processId)) continue;
            var key = groupBy == "method"
                ? $"{ev.MethodNamespace}.{ev.MethodName}"
                : (ev.MethodNamespace?.Split('.').FirstOrDefault() ?? "unknown");
            var existing = jitData.GetValueOrDefault(key);
            jitData[key] = (existing.sizeBytes + ev.MethodSize, existing.count + 1);
        }

        var sorted = sortBy switch
        {
            "count" => jitData.OrderByDescending(kv => kv.Value.count),
            _ => jitData.OrderByDescending(kv => kv.Value.sizeBytes)
        };

        return sorted.Take(take)
            .Select(kv => new JitInfo(kv.Key, kv.Value.sizeBytes, kv.Value.count))
            .ToList();
    }

    #endregion

    #region Exceptions

    public List<ExceptionInfo> GetExceptions(double? startMs, double? stopMs, string? processName, int? processId, int take)
    {
        var exceptions = new Dictionary<string, (int count, string? message)>();

        foreach (var ev in _traceLog.Events.ByEventType<ExceptionTraceData>())
        {
            if (!InRange(ev, startMs, stopMs, processName, processId)) continue;
            var type = ev.ExceptionType ?? "unknown";
            var existing = exceptions.GetValueOrDefault(type);
            exceptions[type] = (existing.count + 1, existing.message ?? ev.ExceptionMessage);
        }

        return exceptions.OrderByDescending(kv => kv.Value.count)
            .Take(take)
            .Select(kv => new ExceptionInfo(kv.Key, kv.Value.count, kv.Value.message))
            .ToList();
    }

    #endregion

    #region Contention

    public List<ContentionInfo> GetContention(double? startMs, double? stopMs, string? processName, int? processId, int take)
    {
        var stackSource = new MutableTraceEventStackSource(_traceLog);
        var sample = new StackSourceSample(stackSource);
        var hasData = false;

        foreach (var ev in _traceLog.Events.ByEventType<ContentionStopTraceData>())
        {
            if (!InRange(ev, startMs, stopMs, processName, processId)) continue;

            var callStack = stackSource.GetCallStack(ev.CallStackIndex(), ev);
            sample.StackIndex = callStack;
            sample.Metric = (float)(ev.DurationNs / 1_000_000.0);
            sample.Count = 1;
            sample.TimeRelativeMSec = ev.TimeStampRelativeMSec;
            stackSource.AddSample(sample);
            hasData = true;
        }

        if (!hasData) return [];

        stackSource.DoneAddingSamples();
        var callTree = new CallTree(ScalingPolicyKind.TimeMetric) { StackSource = stackSource };

        var results = new List<ContentionInfo>();
        foreach (var node in callTree.ByID)
        {
            if (node.ExclusiveMetric <= 0) continue;
            results.Add(new ContentionInfo(node.Name, Math.Round(node.ExclusiveMetric, 2), (int)node.ExclusiveCount));
        }

        return results.OrderByDescending(c => c.DurationMs).Take(take).ToList();
    }

    #endregion

    #region GC

    public List<GcInfo> GetGcEvents(double? startMs, double? stopMs, string? processName, int? processId, int take)
    {
        var gcEvents = new List<GcInfo>();

        foreach (var ev in _traceLog.Events.ByEventType<GCHeapStatsTraceData>())
        {
            if (!InRange(ev, startMs, stopMs, processName, processId)) continue;
            gcEvents.Add(new GcInfo(
                gcEvents.Count + 1,
                ev.GenerationSize0 > 0 ? 0 : (ev.GenerationSize1 > 0 ? 1 : 2),
                0, "HeapStats", ev.TotalHeapSize,
                ev.TimeStampRelativeMSec));
        }

        return gcEvents.Take(take).ToList();
    }

    #endregion

    #region Memory

    public List<MemorySnapshot> GetMemorySnapshots(string? processName)
    {
        return _traceLog.Processes
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .Where(p => processName == null || p.Name.Equals(processName, StringComparison.OrdinalIgnoreCase))
            .Select(p => new MemorySnapshot(p.Name, p.ProcessID, 0, 0, 0))
            .ToList();
    }

    #endregion

    #region File I/O

    public List<FileIoSummary> GetFileIo(double? startMs, double? stopMs, string? processName, int? processId, string ioType, int take)
    {
        var fileStats = new Dictionary<(string file, string proc, int pid), (long readBytes, long writeBytes, int readCount, int writeCount)>();

        var filteredEvents = _traceLog.Events
            .Filter(e => startMs == null || e.TimeStampRelativeMSec >= startMs.Value)
            .Filter(e => stopMs == null || e.TimeStampRelativeMSec <= stopMs.Value);

        foreach (var ev in filteredEvents)
        {
            if (processName != null && !(ev.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (processId.HasValue && ev.ProcessID != processId.Value) continue;

            if (ioType != "write" && ev.EventName.Contains("FileIO/Read"))
            {
                var fileName = GetPayload(ev, "FileName") ?? "unknown";
                var size = GetPayloadLong(ev, "IoSize");
                var key = (fileName, ev.ProcessName ?? "", ev.ProcessID);
                var existing = fileStats.GetValueOrDefault(key);
                fileStats[key] = (existing.readBytes + size, existing.writeBytes, existing.readCount + 1, existing.writeCount);
            }
            else if (ioType != "read" && ev.EventName.Contains("FileIO/Write"))
            {
                var fileName = GetPayload(ev, "FileName") ?? "unknown";
                var size = GetPayloadLong(ev, "IoSize");
                var key = (fileName, ev.ProcessName ?? "", ev.ProcessID);
                var existing = fileStats.GetValueOrDefault(key);
                fileStats[key] = (existing.readBytes, existing.writeBytes + size, existing.readCount, existing.writeCount + 1);
            }
        }

        return fileStats
            .Select(kv => new FileIoSummary(kv.Key.file, kv.Key.proc, kv.Key.pid, kv.Value.readBytes, kv.Value.writeBytes, kv.Value.readCount, kv.Value.writeCount))
            .OrderByDescending(f => f.ReadBytes + f.WriteBytes)
            .Take(take)
            .ToList();
    }

    #endregion

    #region ETW Events & Providers

    public List<ProviderSummary> GetProviders()
    {
        if (_cachedProviders != null) return _cachedProviders;

        var providers = new Dictionary<string, int>();
        foreach (var ev in _traceLog.Events)
        {
            var name = ev.ProviderName ?? "unknown";
            providers[name] = providers.GetValueOrDefault(name) + 1;
        }

        _cachedProviders = providers.OrderByDescending(kv => kv.Value)
            .Select(kv => new ProviderSummary(kv.Key, kv.Value))
            .ToList();

        return _cachedProviders;
    }

    public List<EtwEvent> GetEvents(string providerName, string? eventName, double? startMs, double? stopMs, string? processName, int? processId, int skip, int take)
    {
        var events = new List<EtwEvent>();
        var count = 0;

        var filteredEvents = _traceLog.Events
            .Filter(e => startMs == null || e.TimeStampRelativeMSec >= startMs.Value)
            .Filter(e => stopMs == null || e.TimeStampRelativeMSec <= stopMs.Value);

        foreach (var ev in filteredEvents)
        {
            if (!ev.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (eventName != null && !ev.EventName.Contains(eventName, StringComparison.OrdinalIgnoreCase)) continue;
            if (processName != null && !(ev.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (processId.HasValue && ev.ProcessID != processId.Value) continue;

            count++;
            if (count <= skip) continue;
            if (events.Count >= take) break;

            var payload = new Dictionary<string, string>();
            for (int i = 0; i < ev.PayloadNames.Length && i < 20; i++)
            {
                try { payload[ev.PayloadNames[i]] = ev.PayloadValue(i)?.ToString() ?? ""; }
                catch { /* skip */ }
            }

            events.Add(new EtwEvent(
                Math.Round(ev.TimeStampRelativeMSec, 3),
                ev.ProviderName, ev.EventName,
                ev.ProcessName ?? "", ev.ProcessID,
                payload));
        }

        return events;
    }

    #endregion

    #region Helpers

    private static bool InRange(TraceEvent ev, double? startMs, double? stopMs, string? processName, int? processId)
    {
        if (startMs.HasValue && ev.TimeStampRelativeMSec < startMs.Value) return false;
        if (stopMs.HasValue && ev.TimeStampRelativeMSec > stopMs.Value) return false;
        if (processId.HasValue && ev.ProcessID != processId.Value) return false;
        if (processName != null && !(ev.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        return true;
    }

    private static string? GetPayload(TraceEvent ev, string name)
    {
        var idx = Array.IndexOf(ev.PayloadNames, name);
        return idx >= 0 ? ev.PayloadValue(idx)?.ToString() : null;
    }

    private static long GetPayloadLong(TraceEvent ev, string name)
    {
        var idx = Array.IndexOf(ev.PayloadNames, name);
        return idx >= 0 ? Convert.ToInt64(ev.PayloadValue(idx) ?? 0) : 0;
    }

    private static string BuildSymbolPath(string? bundledPath, string? userPath)
    {
        var parts = new List<string>();
        if (bundledPath != null && Directory.Exists(bundledPath))
            parts.Add(bundledPath);
        if (!string.IsNullOrWhiteSpace(userPath))
            parts.Add(userPath);
        var cache = Path.Combine(Path.GetTempPath(), "ETLReader", "symbols");
        Directory.CreateDirectory(cache);
        parts.Add($"srv*{cache}*https://msdl.microsoft.com/download/symbols");
        return string.Join(";", parts);
    }

    private void ResolveSymbols()
    {
        foreach (var module in _traceLog.ModuleFiles)
        {
            try { _traceLog.CodeAddresses.LookupSymbolsForModule(_symbolReader, module); }
            catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        _symbolReader.Dispose();
        _traceLog.Dispose();
    }

    #endregion
}
