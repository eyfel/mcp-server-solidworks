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
  pairing    -- bend annotation -> bend line. Pure, no dependency.
  lowering   -- the direct-buildable gate + flat-pattern -> IR transcription. Pure, no dependency.
  config.json -- user-adjustable drafting/manufacturing conventions (projection standard, K-factor,
                 bend-note conventions, tolerances) read instead of re-derived per drawing.

IMPORT ORDER MATTERS FOR THE MCP ADAPTER: importing this package pulls ezdxf (and through it numpy),
whose C extensions DEADLOCK when first loaded off the main thread on Windows. The adapter therefore
imports it EAGERLY at startup, never lazily inside a tool. See logs.md ADR-062.
"""
from .contour import chain_view, loop_member_segments
from .pairing import pair_bend_notes
from .lowering import assess, lower_flat_pattern
from .dxf_read import ANALYSIS_VERSION, load_config, read

__all__ = ["read", "load_config", "ANALYSIS_VERSION", "chain_view", "loop_member_segments",
           "pair_bend_notes", "assess", "lower_flat_pattern"]
