# ETLReader

An MCP server for analyzing .NET ETL performance traces. Load traces, prepare them, and query CPU stacks, CLR events, file I/O, memory, and any ETW provider — all through a conversational agent.

## How It Works

ETLReader runs as a stdio MCP server. An LLM agent (GitHub Copilot, Claude, etc.) connects and drives the investigation:

```
1. load_traces    → Load a .etl.zip or raw .etl file into a session
2. prepare_etl    → Extract the ETL binary and build an ETLX index (async, agent polls with progress %)
3. Analysis tools → Query CPU stacks, allocations, I/O, etc.
```

The ETL binary (~700MB) is extracted lazily. The ETLX index is cached so subsequent loads are instant. Symbols are resolved from bundled PDBs + Microsoft symbol server.

## Tools (9)

### Session Management

| Tool | Description |
|------|-------------|
| `load_traces` | Load `.etl.zip`, raw `.etl`, or a folder into a session (baseline and/or target) |
| `session_status` | Show what's currently loaded |
| `prepare_etl` | Extract ETL and build ETLX index (steps: `extract` → `index` → `status`). Reports `progressPercent` during both extraction and indexing. |

### Analysis

| Tool | Description |
|------|-------------|
| `list_processes` | Processes in the trace with CPU time, PIDs, and command lines |
| `get_cpu_stacks` | CPU sampling stacks — `tree` (by-name), `hotpath`, or `diff` (baseline vs target). Filters to PerfInfo/SampledProfile events. |
| `get_clr_data` | CLR runtime: `allocations`, `jit`, `exceptions`, `contention`, `gc`. Requires CLR ETW provider in trace. |
| `get_io_activity` | File I/O reads/writes aggregated by path. Requires FileIO kernel provider in trace. |
| `get_memory` | Process memory snapshots from ProcessCounters events. Not all traces contain this data. |
| `get_etw_events` | Query any ETW provider's events. Omit provider to list all. Supports process filtering. |

All analysis tools support filtering by process name/ID and time range.

### Error Handling

All tools are honest about their data requirements:
- Empty results return `{ "data": [], "note": "..." }` explaining why (e.g., missing ETW provider)
- Errors return `{ "error": "...", "hint": "..." }` with actionable guidance
- No tool returns fake data or fails silently

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

## Agent Profiles

- `github/agents/pr-review.agent.md` — PR review agent profile.

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

## MCP Server Configuration

Copy the contents of `server.json` (repo root) directly into your MCP client config (`.vscode/mcp.json`, `claude_desktop_config.json`, etc.). No installation step needed — `dnx` fetches and runs the tool directly from NuGet.org.

## Publish to NuGet

Releases are fully automated via CI. To publish a new version:

1. Update the version in `Directory.Build.props`
2. Push a tag (name doesn't matter — it's just the trigger):

```powershell
git tag v2.0.4
git push origin v2.0.4
```

The publish workflow will:
1. Read the version from `Directory.Build.props`
2. Build and pack the `.nupkg` with that version
3. Push it to NuGet.org (requires `NUGET_API_KEY` secret in repo settings)
4. Update `server.json` to reference the new version and commit it back to `main`

You can also trigger a publish manually from the **Actions** tab using `workflow_dispatch`.

### Local build

```powershell
.\build.ps1
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
