# Contributing Guide

Thanks for contributing to SolidPilot. This document summarizes how to set up the development environment, the architectural rules, and the flow for adding a new capability.

SolidPilot is an AI-driven CAD automation system for SolidWorks that runs as an MCP server. For an overview, see [README.md](README.md).

---

## Architecture and invariants

The system has four layers, and preserving these boundaries is the foundation of the project:

| Layer | Directory | Responsibility |
|---|---|---|
| `cad-planner` | `cad-planner/` | Intent -> CAD-neutral Feature Graph IR. Does not touch COM, does not emit raw tool calls. |
| `solidworks-compiler` | `solidworks-compiler/` | IR -> tool calls + reference resolution. Deterministic; contains no LLM and no MCP. |
| `solidworks-execution` | `solidworks-execution/` | The only layer that touches SolidWorks COM (C#, .NET 4.8). |
| `adapters/claude` | `adapters/claude/` | MCP protocol bridge (Python, FastMCP). |

Rules that must never be violated:

- The AI works at the **feature level**; only the compiler knows tools; only `SolidWorksService.cs` touches COM.
- **MCP is the top boundary** (between the client and the IR), not an internal transport. The compiler reaches the execution layer over plain REST.
- The IR is **CAD-neutral**; SolidWorks-specific details live only in the compiler and execution layers.
- The execution and planner layers have no knowledge of which client is calling them.
- Adding a new CAD backend means adding a new compiler and execution layer; the IR and `cad-planner` stay unchanged.
- The adapter layer is provider-specific. Supporting a new AI client (OpenClaw, OpenAI, a local LLM, etc.) means only adding a new adapter that reuses the shared bridge core.

---

## Development environment

### Requirements
- Windows and **SolidWorks 2026** (development happens against a local SolidWorks install).
- **.NET Framework 4.8** and MSBuild (ships with Visual Studio 2022).
- **Python 3.x** and **FastMCP** (for the MCP adapter).

### Building and running the execution layer

Build:

```
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" solidworks-execution\SolidworksExecution.sln /t:Build /p:Configuration=Debug
```

Restart (headless, `http://localhost:5000`):

```
Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process solidworks-execution\SolidworksExecution\bin\Debug\SolidworksExecution.exe -WindowStyle Hidden
```

The server runs headless and must be running while SolidWorks is open (the COM connection is established lazily, on the first tool call). C#-side errors and per-request traces are written to `solidworks-execution\SolidworksExecution\bin\Debug\execution.log`.

### Running the adapter

```
cd adapters/claude
pip install -r requirements.txt
python server.py
```

**Important:** the Python MCP adapter does not hot-reload while running. When you change `server.py` (for example, adding a new parameter), the MCP server must be reconnected. Reconnecting also resets the adapter's local `state_version` to 0. (The C# execution server, by contrast, can be restarted.)

---

## Adding a new capability

### A new low-level tool (execution surface)

1. **Contract** — add the tool to `solidworks-execution/contracts/tool-schemas.json`.
2. **Execution** — add a `case "tool_name":` in `ToolController.cs` and implement it in `SolidWorksService.cs`. **Verify any SolidWorks COM API signature by reflecting the interop assembly first — never invent a method name or argument list.** The real API frequently differs from what looks plausible (for example, the model-item insertion API lives on `IDrawingDoc`, not `IView`; `GetLines3` returns empty while `GetPolylines7` is the working geometry getter). Decode unknown return shapes empirically against a live document before writing the parser.
3. **Adapter** — register the MCP tool in `adapters/claude/server.py`. Model-facing guidance lives here (the client never sees `tool-schemas.json`): use `Literal` enums for discriminators, Pydantic `Field` constraints for units and ranges, and real required parameters.
4. **Verify** — run the contract test (see below), then validate against live SolidWorks.

### A new feature (IR level — the strategic direction)

1. Add the feature type to `cad-planner/contracts/feature-graph.schema.json` (this also registers the capability).
2. Add its lowering rule and any required reference resolution in `solidworks-compiler`.
3. It reuses existing low-level tools; usually no new execution tool is needed.

> **Experimental IR tool flag:** the IR entry point `submit_feature_graph` is an experimental **test tool**, gated behind an opt-in kill switch and **disabled by default**. To exercise the IR path, set `SOLIDPILOT_ENABLE_IR=true` in `adapters/claude/.env` (default `false`) and reconnect the adapter; while disabled it stays registered but refuses to run. See `logs-ir.md` (IR-ADR-001).

---

## Conventions

- **Units:** all lengths are in meters (SolidWorks internal units). Angles are taken in degrees at the adapter boundary.
- **HTTP semantics:** all CAD results return HTTP 200; `FAILED` and `DUPLICATE` are domain states, not transport errors. HTTP 400 is only for malformed requests or unknown tool names.
- **state_version:** every request is checked with strict equality; a mismatch returns `FAILED` with `INVALID_STATE_VERSION`. The adapter increments its local value only on a `COMPLETED` response.
- **Variable-length array parameters** must be passed as a JSON string at the MCP boundary (e.g. `points: str = "[]"`), not as a `list`/`List` type. The MCP client stringifies list-typed arguments, which makes a list-typed parameter uncallable; parse the string in the adapter (`json.loads`) or in C#.
- **C# JSON:** parameters are deserialized as a `JObject`; use `p.Value<T>("key")`. Serialization is camelCase and null values are omitted.
- **swconst enums:** must be used as inlined constants, never as runtime types (the relevant DLL is not copied into `bin/Debug`; using one as a type causes a runtime load failure).

---

## Testing

- **Contract test:** catches any tool or parameter drift between `server.py` and `tool-schemas.json`.

  ```
  cd adapters/claude
  python tests/test_schema_contract.py
  ```

  or run it via `pytest`.

- **Live testing (manual by design):** tools are verified against live SolidWorks, with the GUI open, case by case. A cohesive batch of tools is chosen together and tested as a batch. When a tool fails, inspect `execution.log` and report the expected result, the API response, and a hypothesis. For drawing tools, the exported **PDF is the ground truth** — some interop counters under-report (e.g. inserted annotations / center marks), so confirm a drawing change by exporting and reading the PDF rather than trusting an in-band count alone.

There is no behavioral/regression test suite yet; the contract test is the only automated test.

---

## Contributor License Agreement (CLA)

SolidPilot is free and open source under the [GNU AGPL-3.0](LICENSE), while the
project owner also offers a separate **commercial license** for organizations
that cannot comply with the AGPL. For this dual-licensing model to work, every
contribution must come with a clear grant of rights. The full terms live in
**[CLA.md](CLA.md)**; the summary below restates them. **By submitting a
contribution (a pull request, patch, or any other work) you agree to the
following:**

1. **You have the right to contribute it.** The contribution is your original work,
   or you have the necessary rights to submit it, and submitting it does not violate
   any third party's rights or any agreement you are bound by.
2. **License and commercial-use grant.** You grant the project owner a perpetual,
   worldwide, non-exclusive, royalty-free, irrevocable license to use, reproduce,
   modify, distribute, sublicense, and **relicense** your contribution — including
   for **commercial purposes** and as part of a paid or cloud edition — and to
   include it in the project under the current license or any future license the
   owner chooses.
3. **You keep your copyright.** This is a license grant, not an assignment; you
   retain ownership of your contribution and may use it elsewhere.
4. **License adjustment.** You agree that the owner may make the project's license
   more or less permissive as needed, and that your contribution may be distributed
   under such adjusted terms.
5. **As-is.** You provide your contribution without warranty of any kind.

**How to accept:** sign off your commits with `git commit -s` (adds a
`Signed-off-by` line, per [CLA.md](CLA.md)), or include the following line in
your pull request description:

> I have read and agree to the Contributor License Agreement in CLA.md.

> **Note on significant contributions.** For small fixes, the pull-request
> acknowledgment above is sufficient. For a substantial contribution, the owner may
> ask you to also confirm the CLA via a signed document (a wet signature or a secure
> electronic signature) — this makes the grant robust under Turkish copyright law
> (FSEK Art. 52, which requires transfers/licenses of economic rights to be in
> writing and signed). This is not a legal opinion; it is a practical safeguard.

---

## Commits and Pull Requests

- Write meaningful, focused commits; do not mix several unrelated changes in one commit.
- If you change the behavior of a tool or feature, keep the relevant contract and tests up to date.
- In the Pull Request description, state what you changed, why, and how you verified it.
- Include the CLA acknowledgment line (see [Contributor License Agreement](#contributor-license-agreement-cla) above).

For questions and discussions, feel free to open an issue.
