namespace ETLReader.Session;

/// <summary>
/// Holds the active trace session — one baseline and/or one target trace package.
/// Singleton, injected into tools.
/// </summary>
public class TraceSession : IDisposable
{
    public TracePackage? Baseline { get; private set; }
    public TracePackage? Target { get; private set; }
    public bool IsLoaded => Baseline != null || Target != null;
    public bool IsComparison => Baseline != null && Target != null;
    public string? SymbolPath { get; private set; }

    private readonly string _tempRoot;

    public TraceSession()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ETLReader", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Load(string? baselinePath, string? targetPath, string? symbolPath = null)
    {
        if (baselinePath == null && targetPath == null)
            throw new ArgumentException("At least one of baselinePath or targetPath must be provided.");

        // Dispose previous
        Baseline?.Dispose();
        Target?.Dispose();
        Baseline = null;
        Target = null;
        SymbolPath = symbolPath;

        if (baselinePath != null)
            Baseline = LoadPackage(baselinePath, "baseline");

        if (targetPath != null)
            Target = LoadPackage(targetPath, "target");
    }

    /// <summary>
    /// Get the requested side. Defaults to target, falls back to whatever is loaded.
    /// </summary>
    public TracePackage GetSide(string? side = null)
    {
        return side?.ToLowerInvariant() switch
        {
            "baseline" => Baseline ?? throw new InvalidOperationException("No baseline loaded."),
            "target" => Target ?? Baseline ?? throw new InvalidOperationException("No traces loaded."),
            _ => Target ?? Baseline ?? throw new InvalidOperationException("No traces loaded.")
        };
    }

    private TracePackage LoadPackage(string path, string label)
    {
        var sideDir = Path.Combine(_tempRoot, label);
        Directory.CreateDirectory(sideDir);

        // Determine what we're loading
        if (File.Exists(path) && path.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
        {
            var pkg = new TracePackage(path);
            pkg.ExtractMetadata(sideDir);
            return pkg;
        }

        if (File.Exists(path) && path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return TracePackage.FromRawEtl(path);
        }

        if (Directory.Exists(path))
        {
            // Folder: pick first .etl.zip or .etl file
            var zip = Directory.GetFiles(path, "*.etl.zip").OrderBy(f => f).FirstOrDefault();
            if (zip != null)
            {
                var pkg = new TracePackage(zip);
                pkg.ExtractMetadata(sideDir);
                return pkg;
            }

            var etl = Directory.GetFiles(path, "*.etl").OrderBy(f => f).FirstOrDefault();
            if (etl != null)
                return TracePackage.FromRawEtl(etl);
        }

        throw new FileNotFoundException($"No ETL trace found at: {path}");
    }

    public void Dispose()
    {
        Baseline?.Dispose();
        Target?.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); }
            catch { /* best effort */ }
        }
    }
}
