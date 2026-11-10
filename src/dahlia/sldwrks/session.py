"""Module-level free functions for cross-file operations.

The transport is a process-wide singleton, eagerly constructed by
``connect()``. Any dispatching call (``open_file``, ``view_file``,
``File.add_feature``, etc.) raises ``RuntimeError`` if ``connect()``
has not been invoked yet.

Pure construction (``sketch()``, ``extrude()``, pydantic validation,
JSON round-trip) does not require ``connect()``.
"""
from __future__ import annotations

from importlib.metadata import entry_points
from typing import Any, Literal, Optional

from .file import File


# Cached singleton. ``None`` until ``connect()`` runs; cleared by
# ``disconnect()``. Never assigned anywhere else.
_TRANSPORT: Optional[Any] = None


def connect(pid: Optional[int] = None) -> None:
    """Attach to the SOLIDWORKS running on this machine, or with ``pid`` to that instance. Start
    SOLIDWORKS with the Dahlia add-in first. A transport installed under the ``dahlia.transports``
    entry point takes over when its ``from_environment()`` returns one.

    Idempotent: a second call with the singleton already set is a no-op.
    Raises ``RuntimeError`` (with a clear message) if pywin32 is missing,
    SolidWorks isn't running, or the addin isn't loaded.
    """
    global _TRANSPORT
    if _TRANSPORT is not None:
        return
    for plugin in entry_points(group="dahlia.transports"):
        transport = plugin.load().from_environment()
        if transport is not None:
            _TRANSPORT = transport
            return
    # Lazy import so non-Windows environments and pure-construction users
    # don't pull win32com at module-import time.
    from .transport import ComTransport
    _TRANSPORT = ComTransport(pid=pid)


def disconnect() -> None:
    """Drop the singleton transport.

    Primarily used by tests for isolation between cases. After this call,
    any dispatching API will raise until ``connect()`` is invoked again.
    """
    global _TRANSPORT
    close = getattr(_TRANSPORT, "close", None)
    if callable(close):
        close()
    _TRANSPORT = None


def _require_transport() -> Any:
    """Return the singleton transport or raise a clear error."""
    if _TRANSPORT is None:
        raise RuntimeError(
            "dahlia.connect() must be called before any "
            "operation that talks to SolidWorks."
        )
    return _TRANSPORT


def application() -> Any:
    """SOLIDWORKS's own API (``ISldWorks``) as a late-bound pywin32 object, for what Dahlia's
    commands do not cover."""
    return _require_transport().application


def run_script(code: str) -> dict:
    """Compile and run C# inside SOLIDWORKS with ``SwApp`` (``ISldWorks``), ``Session`` and
    ``Workspace`` in scope; the last expression (or ``return``) is the result. Returns
    ``{"result": ..., "output": <printed text>}``."""
    reply = _require_transport().call("RunScript", code=code)
    return {"result": reply.get("Result"), "output": reply.get("Output", "")}


def view_file(path: str) -> dict:
    """Open the file (if needed) and return ViewFile metadata in one call."""
    return _require_transport().call("ViewFile", file=path)


def open_file(path: str) -> File:
    """Open a native part or assembly. Returns a File handle."""
    from pathlib import Path
    r = _require_transport().call("OpenFile", path=str(Path(path).resolve()))
    return File(r["FileName"])


def create_file(kind: Literal["part", "assembly"] = "part") -> File:
    """Create a new untitled part or assembly."""
    if kind not in ("part", "assembly"):
        raise ValueError("kind must be 'part' or 'assembly'")
    r = _require_transport().call("CreateNewFile", **({"kind": kind} if kind != "part" else {}))
    return File(r["FileName"])


def list_files() -> list[File]:
    """List all currently open files."""
    names = _require_transport().call("GetFiles")
    return [File(name) for name in (names or [])]


def get_active_file() -> Optional[File]:
    """Return the currently active file, or None if no file is open."""
    r = _require_transport().call("GetActiveFile")
    if r is None:
        return None
    return File(r["FileName"])


def set_frame_visible(visible: bool) -> dict:
    """Show or hide the SOLIDWORKS main window. Hidden, it stays hidden across opened documents and
    stops refreshing its toolbars, which is most of what parallel instances on one desktop wait on."""
    return _require_transport().call("SetFrameVisible", visible=visible)


def close_all_files() -> dict:
    """Close all open files."""
    return _require_transport().call("CloseAllFiles")
