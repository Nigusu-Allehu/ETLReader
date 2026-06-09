using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ETLReader.Session;

public enum TraceType { PerfMetrics, Warmup, ReferenceSet, Unknown }

/// <summary>
/// Represents a single ETL trace — handles extraction lifecycle and holds the cached analyzer.
/// </summary>
public partial class TracePackage : IDisposable
{
    public string SourcePath { get; }
    public string TestName { get; }
    public TraceType TraceType { get; }
    public int Iteration { get; }

    public string ExtractedDir { get; private set; } = "";
    public string? EtlFilePath { get; private set; }
    public string? SymbolsPath { get; private set; }

    public bool IsEtlExtracted { get; private set; }
    public volatile bool IsExtracting;
    public volatile bool IsIndexing;
    public string? ExtractError { get; set; }
    public string? IndexError { get; set; }

    /// <summary>Progress 0-100 for current operation (extract or index).</summary>
    public int ProgressPercent { get; set; }

    public Analysis.EtlAnalyzer? Analyzer { get; set; }

    public TracePackage(string sourcePath)
    {
        SourcePath = sourcePath;
        var fileName = Path.GetFileName(sourcePath);
        TestName = ParseTestName(fileName);
        TraceType = ParseTraceType(fileName);
        Iteration = ParseIteration(fileName);
    }

    /// <summary>
    /// Create from a raw .etl file already on disk (no zip).
    /// </summary>
    public static TracePackage FromRawEtl(string etlPath)
    {
        var pkg = new TracePackage(etlPath)
        {
            ExtractedDir = Path.GetDirectoryName(etlPath) ?? "",
            EtlFilePath = etlPath,
            IsEtlExtracted = true
        };

        var symbolsDir = Path.Combine(pkg.ExtractedDir, "symbols");
        if (Directory.Exists(symbolsDir))
            pkg.SymbolsPath = symbolsDir;

        return pkg;
    }

    /// <summary>
    /// Extract non-ETL files from the zip (fast — small metadata files only).
    /// </summary>
    public void ExtractMetadata(string baseDir)
    {
        var hash = SourcePath.GetHashCode().ToString("x8");
        ExtractedDir = Path.Combine(baseDir, hash);
        Directory.CreateDirectory(ExtractedDir);

        using var archive = ZipFile.OpenRead(SourcePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            // Remember the ETL path but don't extract it yet
            if (entry.FullName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
            {
                EtlFilePath = Path.Combine(ExtractedDir, entry.FullName);
                continue;
            }

            // Remember symbols path but don't extract yet
            if (entry.FullName.Contains("symbols", StringComparison.OrdinalIgnoreCase))
            {
                SymbolsPath ??= Path.Combine(ExtractedDir,
                    Path.GetDirectoryName(entry.FullName) ?? "symbols");
                continue;
            }

            // Extract everything else (scenarios.xml, configs, etc.)
            var destPath = Path.Combine(ExtractedDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    /// <summary>
    /// Extract the raw ETL binary from the zip (slow — 700MB+). Called on demand.
    /// Reports progress via ProgressPercent.
    /// </summary>
    public void ExtractEtl()
    {
        if (IsEtlExtracted || EtlFilePath == null) return;

        using var archive = ZipFile.OpenRead(SourcePath);

        // Find total size of entries we'll extract
        long totalBytes = 0;
        long writtenBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
                totalBytes += entry.Length;
            if (entry.FullName.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(entry.Name))
                totalBytes += entry.Length;
        }

        foreach (var entry in archive.Entries)
        {
            // Extract ETL
            if (entry.FullName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
            {
                var destPath = Path.Combine(ExtractedDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                ExtractWithProgress(entry, destPath, totalBytes, ref writtenBytes);
                EtlFilePath = destPath;
                IsEtlExtracted = true;
            }

            // Extract symbols alongside
            if (entry.FullName.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(entry.Name))
            {
                var destPath = Path.Combine(ExtractedDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                ExtractWithProgress(entry, destPath, totalBytes, ref writtenBytes);
            }
        }

        ProgressPercent = 100;
    }

    private void ExtractWithProgress(ZipArchiveEntry entry, string destPath, long totalBytes, ref long writtenBytes)
    {
        using var source = entry.Open();
        using var dest = File.Create(destPath);
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            dest.Write(buffer, 0, bytesRead);
            writtenBytes += bytesRead;
            if (totalBytes > 0)
                ProgressPercent = (int)(writtenBytes * 100 / totalBytes);
        }
    }

    public void Dispose()
    {
        Analyzer?.Dispose();
        Analyzer = null;
        if (Directory.Exists(ExtractedDir) && ExtractedDir != Path.GetDirectoryName(SourcePath))
        {
            try { Directory.Delete(ExtractedDir, true); }
            catch { /* best effort */ }
        }
    }

    private static string ParseTestName(string fileName)
    {
        var match = TestNameRegex().Match(fileName);
        return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(fileName);
    }

    private static TraceType ParseTraceType(string fileName)
    {
        if (fileName.Contains("PerfMetrics", StringComparison.OrdinalIgnoreCase)) return TraceType.PerfMetrics;
        if (fileName.Contains("Warmup", StringComparison.OrdinalIgnoreCase)) return TraceType.Warmup;
        if (fileName.Contains("ReferenceSet", StringComparison.OrdinalIgnoreCase)) return TraceType.ReferenceSet;
        return TraceType.Unknown;
    }

    private static int ParseIteration(string fileName)
    {
        var match = IterationRegex().Match(fileName);
        return match.Success && int.TryParse(match.Groups[2].Value, out var i) ? i : 1;
    }

    [GeneratedRegex(@"^(.+?)-(PerfMetrics|Warmup|ReferenceSet)")]
    private static partial Regex TestNameRegex();

    [GeneratedRegex(@"-(PerfMetrics|Warmup|ReferenceSet)-(\d+)")]
    private static partial Regex IterationRegex();
}
