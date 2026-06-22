import os
import subprocess
import threading
import time
import httpx
from config import (
    EXECUTE_ENDPOINT,
    STATE_ENDPOINT,
    HEALTH_ENDPOINT,
    ENSURE_ENDPOINT,
    HTTP_TIMEOUT,
    ENSURE_TIMEOUT,
    EXECUTION_EXE_PATH,
    SERVER_SPAWN_TIMEOUT,
)
from adapter_log import write as _log


class ExecutionLayerError(Exception):
    """Raised when the execution layer returns an unexpected HTTP error."""
    pass


# Serializes auto-start so two concurrent tool calls hitting a down server don't each
# spawn a duplicate exe.
_spawn_lock = threading.Lock()


def _server_is_up() -> bool:
    """Cheap liveness probe — True if /health answers 200."""
    try:
        with httpx.Client(timeout=2.0) as client:
            return client.get(HEALTH_ENDPOINT).status_code == 200
    except Exception:
        return False


def _ensure_server_up() -> None:
    """Start the execution-server exe if it isn't already answering /health.

    Guarded against duplicate spawns: re-checks /health inside the lock, spawns at most
    one headless/detached process, then polls /health until it's up. The server is meant
    to outlive this call (it persists while SolidWorks is open), so it is launched detached.
    Raises ExecutionLayerError if it can't be brought up (missing exe, or never came up).
    """
    with _spawn_lock:
        if _server_is_up():
            return
        if not os.path.isfile(EXECUTION_EXE_PATH):
            _log(f"!! cannot auto-start server — exe not found at {EXECUTION_EXE_PATH}")
            raise ExecutionLayerError(
                "solidworks-execution is not running and its exe was not found at "
                f"{EXECUTION_EXE_PATH}. Build the server or set EXECUTION_EXE_PATH."
            )
        _log(f"-> auto-starting execution server: {EXECUTION_EXE_PATH}")
        # DETACHED_PROCESS | CREATE_NO_WINDOW: survive the adapter, no console window flash.
        creationflags = 0x00000008 | 0x08000000
        try:
            subprocess.Popen(
                [EXECUTION_EXE_PATH],
                cwd=os.path.dirname(EXECUTION_EXE_PATH),
                creationflags=creationflags,
                close_fds=True,
            )
        except Exception as ex:
            _log(f"!! server spawn failed: {ex}")
            raise ExecutionLayerError(f"Failed to start solidworks-execution: {ex}")

        deadline = time.monotonic() + SERVER_SPAWN_TIMEOUT
        while time.monotonic() < deadline:
            if _server_is_up():
                _log("<- execution server is up")
                return
            time.sleep(0.5)
        _log("!! execution server did not answer /health within timeout")
        raise ExecutionLayerError(
            f"Started solidworks-execution but it did not answer {HEALTH_ENDPOINT} "
            f"within {SERVER_SPAWN_TIMEOUT}s."
        )


def _request_with_autostart(do_request, label: str):
    """Run an httpx request; on ConnectError, auto-start the server once and retry.

    Makes every tool call self-heal the "server is down" case transparently. Timeouts are
    NOT caught here (they propagate to each caller's own timeout handling).
    """
    try:
        return do_request()
    except httpx.ConnectError:
        _log(f"<- {label} CONNECT_ERROR — attempting server auto-start")
        _ensure_server_up()  # raises ExecutionLayerError if it can't bring the server up
        try:
            return do_request()
        except httpx.ConnectError:
            _log(f"<- {label} CONNECT_ERROR after auto-start")
            raise ExecutionLayerError(
                "Cannot connect to solidworks-execution even after auto-start. "
                f"Is {HEALTH_ENDPOINT} reachable?"
            )


def get_health() -> dict:
    """GET /health — server status + COM attach state (does not touch state_version)."""
    try:
        with httpx.Client(timeout=HTTP_TIMEOUT) as client:
            response = client.get(HEALTH_ENDPOINT)
    except httpx.ConnectError:
        _log("<- health CONNECT_ERROR (server down?)")
        raise ExecutionLayerError(
            f"Cannot connect to solidworks-execution. Is the server running on {HEALTH_ENDPOINT}?"
        )
    except httpx.TimeoutException:
        _log("<- health TIMEOUT")
        raise ExecutionLayerError(f"Health request timed out after {HTTP_TIMEOUT}s.")
    if response.status_code != 200:
        raise ExecutionLayerError(f"Unexpected HTTP {response.status_code} from /health: {response.text}")
    body = response.json()
    _log(f"<- health status={body.get('status')} comAttached={body.get('comAttached')} sv={body.get('stateVersion')}")
    return body


def get_state() -> int:
    """
    GET /api/tool/state — fetch the current authoritative state_version.

    Used to resync after a desync (e.g. execution server restarted on rebuild).
    Read-only on the server: no state_version check, no increment.
    """
    _log("-> get_state (resync)")

    def _do():
        with httpx.Client(timeout=HTTP_TIMEOUT) as client:
            return client.get(STATE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "get_state")
    except httpx.TimeoutException:
        _log("<- get_state TIMEOUT")
        raise ExecutionLayerError(
            f"Resync request to solidworks-execution timed out after {HTTP_TIMEOUT}s."
        )

    if response.status_code != 200:
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /state: {response.text}"
        )

    sv = int(response.json().get("stateVersion", 0))
    _log(f"<- get_state sv={sv}")
    return sv


def call_tool(tool_name: str, operation_id: str, state_version: int, params: dict) -> dict:
    """
    POST /api/tool/execute on the solidworks-execution layer.

    Returns the parsed JSON response body on HTTP 200.
    Raises ExecutionLayerError on HTTP 400 or unexpected status codes.
    """
    payload = {
        "operationId": operation_id,
        "tool": tool_name,
        "stateVersion": state_version,
        "params": params,
    }

    _log(f"-> {tool_name} op={operation_id} sv={state_version}")

    def _do():
        with httpx.Client(timeout=HTTP_TIMEOUT) as client:
            return client.post(EXECUTE_ENDPOINT, json=payload)

    try:
        response = _request_with_autostart(_do, tool_name)
    except httpx.TimeoutException:
        _log(f"<- {tool_name} TIMEOUT after {HTTP_TIMEOUT}s")
        raise ExecutionLayerError(
            f"Request to solidworks-execution timed out after {HTTP_TIMEOUT}s."
        )

    if response.status_code == 400:
        body = response.json()
        _log(f"<- {tool_name} HTTP_400 {body.get('error', '')}")
        raise ExecutionLayerError(f"Bad request: {body.get('error', response.text)}")

    if response.status_code != 200:
        _log(f"<- {tool_name} HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from execution layer: {response.text}"
        )

    body = response.json()
    err = (body.get("error") or {}).get("code")
    _log(f"<- {tool_name} {body.get('status')}{(' ' + err) if err else ''} sv={body.get('stateVersion')}")
    return body


def ensure_ready() -> dict:
    """POST /ensure_ready — bring the whole stack up and report readiness.

    Two layers: (1) make sure the execution server itself is running (auto-spawn it if
    down — a down server can't answer /ensure_ready); (2) the server attaches to SolidWorks,
    launching it via COM if it's closed. Uses a long timeout because a cold SolidWorks launch
    can take tens of seconds. Returns the parsed readiness dict; does NOT open any document.
    """
    _log("-> ensure_ready")
    # A down server can't answer /ensure_ready, so spawn it up front (idempotent / guarded).
    if not _server_is_up():
        _ensure_server_up()

    def _do():
        with httpx.Client(timeout=ENSURE_TIMEOUT) as client:
            return client.post(ENSURE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "ensure_ready")
    except httpx.TimeoutException:
        _log("<- ensure_ready TIMEOUT")
        raise ExecutionLayerError(
            f"ensure_ready timed out after {ENSURE_TIMEOUT}s (a SolidWorks cold launch can be slow)."
        )

    if response.status_code != 200:
        _log(f"<- ensure_ready HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /ensure_ready: {response.text}"
        )

    body = response.json()
    _log(
        f"<- ensure_ready comAttached={body.get('comAttached')} "
        f"swLaunched={body.get('swLaunched')} doc={body.get('activeDocument')}"
    )
    return body
