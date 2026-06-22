"""Minimal file logger for the MCP adapter (P0.6).

Writes to adapter.log next to this module. A FILE (not stdout) because the MCP
server speaks JSON-RPC over stdio — anything on stdout would corrupt the protocol.
Mirrors the C# execution-side ExecLog so the two logs line up per request.

Must NEVER raise — logging failures are swallowed.
"""
import os
import threading
from datetime import datetime

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "adapter.log")
_lock = threading.Lock()


def write(message: str) -> None:
    try:
        stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
        with _lock:
            with open(LOG_PATH, "a", encoding="utf-8") as fh:
                fh.write(f"{stamp}  {message}\n")
    except Exception:
        # logging must never break a tool call
        pass
