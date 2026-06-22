import os
from dotenv import load_dotenv

load_dotenv()

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
