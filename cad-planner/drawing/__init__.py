"""cad-planner/drawing -- the 2D front end of the reverse path.

A technical drawing is design intent expressed in 2D, so this sits in `cad-planner`, the CAD-neutral
intent layer: it reads a DXF/DWG into the compact `draw` dialect, chains its primitives into
contours, and (for the cases where that is fully deterministic) lowers the result straight to
Feature Graph IR. Everything below the IR -- tools, COM, SolidWorks -- stays out of here.

    DXF/DWG -> dxf_read (`draw` dialect) -> contour (loops) -> lowering (IR) -> pycompiler -> execution

Modules:
  dxf_read   -- DXF -> `draw` dialect (needs ezdxf). Version in ANALYSIS_VERSION. The ONLY module
                that touches DXF; everything after it is pure, so the offline gate runs anywhere.
  contour    -- Tier A contour chaining: closed loops + open chains. Pure geometry, no dependency.
  curvefit   -- exploded curve fans -> parametric ellipses, residual-gated. Pure, no dependency.
  viewgraph  -- frame plausibility, graded alignment, the axis solver. Pure, no dependency.
  pairing    -- bend annotation -> bend line. Pure, no dependency.
  lowering   -- the direct-buildable gate + flat-pattern -> IR transcription. Pure, no dependency.
  wire       -- the single boundary between the record shape the pipeline works in and the compact
                shape the model and the saved artifact carry. Pure. `encode`/`decode` are exact
                inverses, so nothing above this line has to know the payload got smaller.
  config.json -- user-adjustable drafting/manufacturing conventions (projection standard, K-factor,
                 bend-note conventions, tolerances, curve-fit thresholds) read instead of
                 re-derived per drawing.

IMPORT ORDER MATTERS FOR THE MCP ADAPTER: importing this package pulls ezdxf (and through it numpy),
whose C extensions DEADLOCK when first loaded off the main thread on Windows. The adapter therefore
imports it EAGERLY at startup, never lazily inside a tool. See logs.md ADR-062.
"""
from .contour import chain_view, loop_member_segments
from .pairing import pair_bend_notes
from .lowering import assess, lower_flat_pattern
from .wire import decode as wire_decode, encode as wire_encode
from .dxf_read import ANALYSIS_VERSION, load_config, read

__all__ = ["read", "load_config", "ANALYSIS_VERSION", "chain_view", "loop_member_segments",
           "pair_bend_notes", "assess", "lower_flat_pattern", "wire_encode", "wire_decode"]
