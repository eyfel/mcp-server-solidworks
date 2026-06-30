import os
from dotenv import load_dotenv

# Load adapters/claude/.env by EXPLICIT path. A bare load_dotenv() searches the current working
# directory, but the MCP host launches server.py from an arbitrary cwd, so the adapter's own .env
# (e.g. SOLIDPILOT_ENABLE_IR) was silently missed. Anchor it to this file's directory.
load_dotenv(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"))

EXECUTION_BASE_URL = os.getenv("EXECUTION_BASE_URL", "http://localhost:5000")
EXECUTE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/execute"
STATE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/state"
HEALTH_ENDPOINT = f"{EXECUTION_BASE_URL}/health"
ENSURE_ENDPOINT = f"{EXECUTION_BASE_URL}/ensure_ready"
HTTP_TIMEOUT = float(os.getenv("HTTP_TIMEOUT", "30"))
# ensure_ready may cold-launch SolidWorks, which can take tens of seconds — give it room.
ENSURE_TIMEOUT = float(os.getenv("ENSURE_TIMEOUT", "120"))

# Auto-start of the execution server (so the user never has to launch the exe by hand).
# Default points at the standard Debug build output, two dirs up from this adapter package
# (adapters/claude → repo root → solidworks-execution/...). Override via .env if needed.
_DEFAULT_EXE = os.path.normpath(
    os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "..",
        "solidworks-execution", "SolidworksExecution", "bin", "Debug", "SolidworksExecution.exe",
    )
)
EXECUTION_EXE_PATH = os.getenv("EXECUTION_EXE_PATH", _DEFAULT_EXE)
# How long to wait for a freshly-spawned server to answer /health before giving up.
SERVER_SPAWN_TIMEOUT = float(os.getenv("SERVER_SPAWN_TIMEOUT", "20"))

# Experimental Feature Graph IR tool (submit_feature_graph) — OPT-IN, operator-controlled kill
# switch (gate layer 1). Default OFF: the tool is ALWAYS registered (so the contract test is
# deterministic and tool discovery is stable) but refuses at runtime unless this is enabled.
# Test users opt in with SOLIDPILOT_ENABLE_IR=true. See logs-ir.md (IR-ADR-001).
ENABLE_EXPERIMENTAL_IR = os.getenv("SOLIDPILOT_ENABLE_IR", "false").strip().lower() in (
    "1", "true", "yes", "on")
