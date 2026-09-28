import os
from dotenv import load_dotenv

# Load adapters/claude/.env by EXPLICIT path. A bare load_dotenv() searches the current working
# directory, but the MCP host launches server.py from an arbitrary cwd, so the adapter's own .env
# was silently missed. Anchor it to this file's directory.
load_dotenv(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"))

# Keep the host spelled `localhost`: the C# server registers the HTTP.sys prefix
# `http://localhost:5000/`, which matches on the HOST HEADER, so a request addressed to
# 127.0.0.1 is answered with HTTP 400 by HTTP.sys before it ever reaches our code (measured
# 2026-09-28). Switching to the literal IP would shave the dual-stack connect tax of
# KNOWN-LIMITATIONS #31, but only together with a C# prefix change that needs a urlacl.
EXECUTION_BASE_URL = os.getenv("EXECUTION_BASE_URL", "http://localhost:5000")
EXECUTE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/execute"
STATE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/state"
HEALTH_ENDPOINT = f"{EXECUTION_BASE_URL}/health"
ENSURE_ENDPOINT = f"{EXECUTION_BASE_URL}/ensure_ready"
HTTP_TIMEOUT = float(os.getenv("HTTP_TIMEOUT", "30"))
# ensure_ready may cold-launch SolidWorks, which can take tens of seconds — give it room.
ENSURE_TIMEOUT = float(os.getenv("ENSURE_TIMEOUT", "120"))
# The CONNECT phase gets its own budget, deliberately NOT derived from the timeouts above.
# httpx applies a connect timeout per resolved address, and whichever wins the race decides the
# EXCEPTION TYPE: beat the OS refusal and you get ConnectError, lose and you get ConnectTimeout.
# `_request_with_autostart` catches only the former, so on a host where a refused local connect is
# slow (KNOWN-LIMITATIONS #31 measures 2.04 s on this machine) a small HTTP_TIMEOUT would silently
# disable the adapter's auto-start. A fixed floor makes the exception type independent of config.
CONNECT_TIMEOUT = float(os.getenv("CONNECT_TIMEOUT", "10"))

# Auto-start of the execution server (so the user never has to launch the exe by hand).
# Default points at the standard Debug build output, two dirs up from this adapter package
# (adapters/claude → repo root → execution/solidworks/...). Override via .env if needed.
_DEFAULT_EXE = os.path.normpath(
    os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "..",
        "execution", "solidworks", "SolidworksExecution", "bin", "Debug", "SolidworksExecution.exe",
    )
)
EXECUTION_EXE_PATH = os.getenv("EXECUTION_EXE_PATH", _DEFAULT_EXE)
# How long to wait for a freshly-spawned server to answer /health before giving up.
SERVER_SPAWN_TIMEOUT = float(os.getenv("SERVER_SPAWN_TIMEOUT", "20"))
