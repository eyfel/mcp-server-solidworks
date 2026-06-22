"""
Maps solidworks-execution ExecutionResponse payloads to MCP tool result strings.

COMPLETED → success text with state summary
FAILED    → raises RuntimeError (fastmcp surfaces this as isError=True)
DUPLICATE → success text noting idempotent result
"""
import json


def map_response(response: dict) -> str:
    """
    Convert an ExecutionResponse dict into a MCP-compatible result string.

    Raises RuntimeError for FAILED responses so fastmcp marks the call as an error.
    """
    status = response.get("status")

    if status == "COMPLETED":
        state = response.get("cadState") or {}
        text = (
            f"COMPLETED | state_version={response.get('stateVersion')} | "
            f"document={state.get('activeDocument')} | "
            f"sketch={state.get('activeSketch')} | "
            f"features={state.get('features', [])}"
        )
        # In-band echo of the REAL geometry a create tool just produced (read back from SW,
        # not the input) so the host can self-verify without a separate analyze round-trip.
        # Read-only — does not affect state_version. Only present on tools that populate it.
        result_geometry = response.get("result_geometry")
        if result_geometry is not None:
            text += f" | result_geometry={json.dumps(result_geometry)}"
        return text

    if status == "DUPLICATE":
        return (
            f"DUPLICATE | operation already executed | "
            f"last_known_state_version={response.get('last_known_state_version')}"
        )

    if status == "FAILED":
        error = response.get("error") or {}
        raise RuntimeError(
            f"CAD operation failed | code={error.get('code')} | "
            f"message={error.get('message')}"
        )

    raise RuntimeError(f"Unknown execution response status: {status}")
