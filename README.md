# ETLReader

An MCP server for analyzing .NET ETL performance traces. Load traces, prepare them, and query CPU stacks, CLR events, file I/O, memory, and any ETW provider — all through a conversational agent.

## How It Works

ETLReader runs as a stdio MCP server. An LLM agent (GitHub Copilot, Claude, etc.) connects and drives the investigation:

```
1. load_traces    → Load a .etl.zip or raw .etl file into a session
2. prepare_etl    → Extract the ETL binary and build an ETLX index (async, agent polls)
3. Analysis tools → Query CPU stacks, allocations, I/O, etc.
```

The ETL binary (~700MB) is extracted lazily. The ETLX index is cached so subsequent loads are instant. Symbols are resolved from bundled PDBs + Microsoft symbol server.

## Tools (9)

### Session Management

| Tool | Description |
|------|-------------|
| `load_traces` | Load `.etl.zip`, raw `.etl`, or a folder into a session (baseline and/or target) |
| `session_status` | Show what's currently loaded |
| `prepare_etl` | Extract ETL and build ETLX index (steps: `extract` → `index` → `status`) |

### Analysis

| Tool | Description |
|------|-------------|
| `list_processes` | Processes in the trace with CPU time, PIDs, and command lines |
| `get_cpu_stacks` | CPU sampling stacks — `tree` (by-name), `hotpath`, or `diff` (baseline vs target) |
| `get_clr_data` | CLR runtime: `allocations`, `jit`, `exceptions`, `contention`, `gc` |
| `get_io_activity` | File I/O reads/writes aggregated by path |
| `get_memory` | Process memory snapshots |
| `get_etw_events` | Query any ETW provider's events. Omit provider to list all. |

All analysis tools support filtering by process name/ID and time range.

## Project Structure

```
src/
├── Program.cs              Entry point — MCP server bootstrap
├── Session/
│   ├── TraceSession.cs     Session state (baseline/target)
│   └── TracePackage.cs     Single trace lifecycle (extraction, state)
├── Analysis/
│   └── EtlAnalyzer.cs     TraceEvent wrapper — all ETL queries
└── Tools/
    ├── SessionTools.cs     load_traces, session_status, prepare_etl
    └── AnalysisTools.cs    list_processes, get_cpu_stacks, get_clr_data, etc.
```

## Build

```powershell
dotnet build
```

Or use the build script to produce a `.nupkg`:

```powershell
.\build.ps1
```

Output goes to `package/ETLReader.1.0.0.nupkg`.

## Run Locally

```powershell
dotnet run --project src
```

Or configure in `.vscode/mcp.json`:

```json
{
  "servers": {
    "ETLReader": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/ETLReader/src"]
    }
  }
}
```

## Publish to NuGet

```powershell
.\build.ps1
dotnet nuget push package\ETLReader.1.0.0.nupkg --api-key <KEY> --source https://api.nuget.org/v3/index.json
```

Once published, users install with:

```powershell
dotnet tool install -g ETLReader
```

And configure in their MCP settings:

```json
{
  "servers": {
    "ETLReader": {
      "type": "stdio",
      "command": "dnx",
      "args": ["ETLReader@2.0.1", "--source", "https://api.nuget.org/v3/index.json", "--yes"]
    }
  }
}
```

## Dependencies

| Package | Purpose |
|---------|---------|
| `ModelContextProtocol` | MCP C# SDK (tools, stdio transport) |
| `Microsoft.Extensions.Hosting` | .NET generic host lifecycle |
| `Microsoft.Diagnostics.Tracing.TraceEvent` | Raw ETL parsing (PerfView engine) |

## Requirements

- .NET 10
- Windows (ETL is a Windows trace format; TraceEvent can parse on any OS but traces are collected on Windows)
