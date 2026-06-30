# SolidPilot

**AI-driven CAD automation for SolidWorks — an MCP (Model Context Protocol) server.**

SolidPilot lets an AI model work with SolidWorks at the **CAD feature level**. The goal is for the model to reason in terms of "which CAD intent am I realizing?" instead of "which API method should I call?". Intent is converted into a CAD-neutral intermediate representation, and a deterministic compiler lowers that representation into concrete SolidWorks operations.

SolidPilot is **not** a Claude-only plugin; it is **a general bridge between SolidWorks and AI.** Because MCP is an open standard, any MCP-capable AI client can connect — alongside Claude, OpenClaw, OpenAI-based agents, and local LLMs are also targeted. The architecture was designed for this extensibility **from the start**: the execution and planner layers do not know which client is calling them; a thin adapter per client reuses a shared bridge core. `adapters/claude/` is the current implementation; supporting a new AI client means only adding a new adapter.

> Repository: `mcp-server-solidworks` · Public name: **SolidPilot** · Target version: **SolidWorks 2026**

---

## Core Idea

The SolidWorks API exposes thousands of methods. Presenting each one to the AI as a separate "tool" explodes context size and token cost — the economic problem that stalls similar projects.

SolidPilot solves this by **raising the level of abstraction**:

- The AI produces intent at the **feature level** (for example, "put a hole in the top face").
- That intent is expressed as a CAD-neutral **Feature Graph IR**.
- A deterministic **compiler** lowers the IR into ordered, concrete SolidWorks operations.
- A single feature therefore maps to many low-level operations, and one model call per request is enough.

---

## Architecture

```mermaid
flowchart TD
    U(["User"])

    subgraph PLAN["cad-planner — Planner / Intent · CAD-neutral · Phase 1"]
        AI["AI client<br/>Claude · OpenClaw · OpenAI · local LLM"]
        IR["Feature Graph IR<br/>feature-graph.schema.json"]
        AI --> IR
    end

    subgraph ADAPT["adapters/* — MCP Bridge · MCP BOUNDARY = top"]
        AC["adapters/claude<br/>FastMCP · stdio"]
    end

    subgraph COMP["solidworks-compiler — Deterministic Compiler · no LLM"]
        CO["Lowering + Reference Resolver"]
    end

    subgraph EXE["solidworks-execution — Execution · C# .NET 4.8 · the ONLY COM-touching layer"]
        EX["Low-level tools<br/>idempotency · state_version"]
    end

    SW(["SolidWorks"])

    U --> AI

    %% Target path (planned)
    IR -. "MCP: submit_feature_graph" .-> AC
    AC -. "REST" .-> CO
    CO -. "REST" .-> EX

    %% Current working path (transitional)
    AI == "MCP: low-level tools" ==> AC
    AC == "REST" ==> EX

    EX -- "COM" --> SW
```

In the diagram, a dashed line is the target architecture (the planned IR + compiler path) and a thick line is the current working path (the AI calls the low-level tools directly; the compiler is not yet in the loop).

The system has four layers:

| Layer | Directory | Language | Responsibility |
|---|---|---|---|
| Planner / Intent | `cad-planner/` | AI model + IR schema | Turns user intent into a CAD-neutral Feature Graph IR. Never touches COM, never emits raw tool calls. |
| Compiler | `solidworks-compiler/` | Deterministic (no LLM) | Lowers the IR into ordered tool calls; resolves semantic references (e.g. `top_face`, `center`) against live geometry state. |
| Execution | `solidworks-execution/` | C# (.NET Framework 4.8) | The **only** layer that touches the SolidWorks COM API. The single source of truth for CAD state. |
| Adapter | `adapters/claude/` | Python (FastMCP) | MCP protocol bridge. The MCP boundary sits at the **top** of the system. |

**MCP sits at the top:** it is the boundary where the AI client meets the system, not an internal transport. Everything below the IR is deterministic and communicates over plain REST.

The `adapters/` layer is provider-specific and replaceable. Because the execution and planner layers do not know which client is calling, adding a new AI client (OpenClaw, OpenAI, a local LLM, etc.) means only writing a new adapter — the IR, compiler, and execution layers stay unchanged.

**Target vs. current:** the Feature Graph IR and compiler are designed but not yet built. Today the AI client uses the **low-level MCP tools** directly (the thick path); these tools will collapse into a single `submit_feature_graph` tool once the compiler lands.

---

## Tool List

The execution layer currently exposes roughly **three dozen** low-level **tools** (the set keeps growing); a contract test keeps the adapter and the execution contract in exact sync (see [CONTRIBUTING.md](CONTRIBUTING.md)). All lengths are in meters (SolidWorks internal units).

### Document and lifecycle
- `ensure_ready` — launches SolidWorks via COM and attaches if it is closed (does not open a document).
- `open_new_part` — opens a new part document.
- `open_document` — opens an existing file from disk (native `.sldprt`/`.sldasm`/`.slddrw`; imports `.ipt`/`.CATPart`/STEP/IGES via 3D Interconnect when the translator is available, otherwise returns a clear `OPEN_FAILED`).
- `activate_document` — switches between open documents.
- `save_document` — saves the part or drawing to disk.
- `close_document` — closes the document.

### Sketch
- `create_sketch` — starts a sketch on a plane or a selected face.
- `edit_sketch` — reopens an existing sketch for editing.
- `add_sketch_entity` — adds a sketch entity: line, circle, arc, center arc, ellipse, spline, rectangle, fillet, chamfer.
- `add_sketch_constraint` — adds a sketch relation (horizontal, coincident, etc.).
- `add_dimension` — adds a dimension to the sketch.

### Feature and solid modeling
- `extrude_feature` — boss, cut, revolve, sweep, loft.
- `add_edge_feature` — fillet or chamfer on a solid edge.
- `add_reference_geometry` — reference plane, axis, or point.
- `create_pattern` — linear or circular pattern.
- `sheet_metal_feature` — sheet metal: base_flange, edge_flange, flat_pattern.

### Editing
- `modify_dimension` — changes the value of a named dimension (the basis for variants).
- `edit_feature` — suppresses, unsuppresses, deletes, or renames a feature.

### Material
- `set_part_material` — assigns a material to the part.

### Analysis and query
- `analyze_model` — `geometry`, `mass_properties`, `features`, `edges`, `faces`, `sketch` modes.
- `get_selection` — reads the geometry the user selected in the SolidWorks GUI and maps it to the analyze index.
- `verify_state` — returns the current state and feature tree.

### Drawing
The drawing tools were added after the initial part-modeling set and are now a substantial — though still maturing — capability. They are enough to take a model to a dimensioned multi-view drawing, and to read a drawing back for reverse-engineering.

- `create_drawing` — creates a drawing document (A3 sheet).
- `add_drawing_view` — adds a model view: `front`, `top`, `right`, `isometric`, `back`, `bottom`, `left`.
- `add_flat_pattern_view` — adds a sheet-metal **flat-pattern** view (the unfolded blank with bend lines and bend notes); the correct, standard way to detail sheet-metal parts.
- `auto_dimension_drawing` — transfers the model's driving dimensions into the views (the "Insert Model Items" automation) — the robust alternative to placing dimensions by coordinate.
- `auto_center_marks` — automatically inserts center marks and centerlines on every hole/slot.
- `add_hole_callout` — adds a hole callout on a hole edge.
- `add_drawing_dimension` — adds a single dimension by sheet coordinate.
- `add_section_view` — section view (**experimental**; the API path works on a clean drawing state but is not yet reliable under automation — see Project Status).
- `analyze_drawing` — reads the active drawing structurally: per-view name/type/scale/position and its dimensions; with `include_geometry`, it also returns each view's **projected 2D geometry as clean primitives** (lines and curves), which is the clean shape used to reverse-engineer a part from its drawing independently of dimension-line clutter.

### Export
- `export_document` — STEP, IGES, STL, **PDF, DWG, DXF** (PDF/DWG/DXF require a drawing document).
- `batch_export` — batch export.

---

## Installation and Running

### Requirements
- Windows and **SolidWorks 2026**.
- **.NET Framework 4.8** and MSBuild for the execution layer (ships with Visual Studio 2022).
- **Python 3.x** and **FastMCP** for the adapter (Python dependencies are installed via `requirements.txt`).
- An MCP-capable AI client (e.g. Claude Desktop; OpenClaw, OpenAI-based agents, and local LLMs are also targeted).

### Execution layer (C#)
Build the solution:

```
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" solidworks-execution\SolidworksExecution.sln /t:Build /p:Configuration=Debug
```

Run the server (headless, `http://localhost:5000`):

```
Start-Process solidworks-execution\SolidworksExecution\bin\Debug\SolidworksExecution.exe -WindowStyle Hidden
```

### Adapter (Python)

```
cd adapters/claude
pip install -r requirements.txt
python server.py
```

The adapter connects to the execution layer at `EXECUTION_BASE_URL` (default `http://localhost:5000`; override via `.env`).

### Registering with an AI client (How to Install)

The adapter is registered with an MCP-capable AI client, which launches `server.py` itself. For **Claude Desktop**, the config file is at:

```
C:\Users\<username>\AppData\Roaming\Claude\claude_desktop_config.json
```

Add the following entry under `mcpServers`:

```json
"SolidPilot": {
  "args": ["C:\\Users\\<username>\\Desktop\\MCP Server\\adapters\\claude\\server.py"]
}
```

Update the path to match your own system.

**Restart Claude Desktop after any config change.**

> Because MCP is an open standard, OpenClaw, OpenAI-based agents, or clients running a local LLM can connect the same way by pointing to the same `server.py` adapter.

---

## Project Status

SolidPilot is a **working prototype / early alpha**. The low-level tools have been verified end-to-end against live SolidWorks; all COM calls are serialized on a single dedicated STA thread.

**Parts:** the part-modeling surface is the most mature — sketches, extrude/revolve/sweep/loft, fillets/chamfers, patterns, sheet metal, reference geometry, plus editing (`modify_dimension`, `edit_feature`) and rich analysis. Initially only the tools needed for part creation existed.

**Technical drawing:** added later and now a real (if still maturing) capability — multi-view drawings, model-item auto-dimensioning, center marks, hole callouts, sheet-metal flat-pattern views, and a structural drawing reader. The reverse direction (**drawing → model**) has been demonstrated: a part reconstructed from its drawing alone (read via `analyze_drawing(include_geometry)`) matched the original exactly in volume, surface area, and topology. Section views are experimental and not yet reliable under automation.

**Feature Graph IR + compiler (the strategic target):** designed but **not yet built**. The IR schema is drafted (`cad-planner/contracts/feature-graph.schema.json`, DRAFT v0), and an early experimental path exists — a single `submit_feature_graph` tool plus a Python compiler prototype with offline tests — but the **reference resolver** (the critical, make-or-break module) and full lowering are not implemented. This effort is tracked in its own ledger (`logs-ir.md`).

> **Enabling the experimental IR tool:** `submit_feature_graph` is an experimental **test tool** and is **disabled by default**. To try it, set `SOLIDPILOT_ENABLE_IR=true` in the adapter's `.env` (default `false`) and reconnect the MCP server. While disabled, the tool is still registered but refuses to run; the matured low-level tools are unaffected either way.

**Testing:** a contract test (`adapters/claude/tests/test_schema_contract.py`) fails on any tool/parameter drift between the adapter (`server.py`) and the execution contract (`tool-schemas.json`); it is the only automated test. Behavioral verification is manual against live SolidWorks, by design.

Notes:
- The Python MCP adapter does not hot-reload while running; after editing `server.py`, the MCP server must be reconnected.

---

## Roadmap

The project is under active development. The main next goals:

- **Feature Graph IR and deterministic compiler** — collapsing the low-level tools under a single feature-level interface (`submit_feature_graph`).
- **Reference resolver / persistent naming** — reliable resolution of semantic references (`top_face`, `center`, etc.) against live geometry; the project's critical module.

Support is also being developed in the following areas and is coming soon:

- **Technical drawing:** the core drawing tools exist; remaining work is reliable section views, GD&T / datums, title blocks, detail views, and a bill of materials (BOM).
- **Assembly:** component insertion, mates, component management, and BOM — the next domain to tackle.
- **Analysis:** engineering analysis support.

---

## Contributing

For contribution guidelines, development environment setup, and the guide to adding new capabilities, see [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

Released under the [MIT License](LICENSE).
