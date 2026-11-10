"""COM transport — pywin32 dispatcher into the in-process SolidWorks plugin.

Wire conventions:
  - Top-level command-arg keys are PascalCase (FileName, Index, Feature, ...).
  - Payload contents (inside Feature, Ray, etc.) stay snake_case.
  - Errors arrive as {"Error": "<message>"} from the plugin's outer try/catch.

This module is import-safe on non-Windows (pywin32 is lazy-imported inside
``ComTransport.__init__``).
"""
from __future__ import annotations

import contextlib
import json
import logging
from typing import Any, Optional

logger = logging.getLogger(__name__)


class PluginError(RuntimeError):
    """Raised when the plugin returns ``{"Error": "..."}``."""


# Fixed snake_case -> PascalCase map for the command-arg boundary. Anything
# not listed falls back to the snake-to-pascal helper below.
_RENAME = {
    "file": "FileName",
    "index": "Index",
    "feature": "Feature",
    "ray": "Ray",
    "entity_type": "EntityType",
    "units": "Units",
    "body_ids": "BodyIds",
    "path": "Path",
    "orientation": "Orientation",
}


_SW_PROGID = "SldWorks.Application"


def _snake_to_pascal(name: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in name.split("_") if part)


def _wire_key(name: str) -> str:
    return _RENAME.get(name, _snake_to_pascal(name))


def _json_default(obj: Any) -> Any:
    """JSON fallback: pydantic-ish ``model_dump`` if present, else TypeError."""
    dump = getattr(obj, "model_dump", None)
    if callable(dump):
        return dump()
    raise TypeError(f"Object of type {type(obj).__name__} is not JSON serializable")


# Normalize outgoing floats for deterministic serialization and stable geometry references.
_WIRE_SIG_FIGS = 13
_WIRE_ZERO_SNAP = 1e-12


def _round_sig13(x: float) -> float:
    if -_WIRE_ZERO_SNAP < x < _WIRE_ZERO_SNAP:
        return 0.0
    return float(f"{x:.{_WIRE_SIG_FIGS}g}")


def _round_floats_wire(obj: Any) -> Any:
    if isinstance(obj, bool):
        return obj
    if isinstance(obj, float):
        return _round_sig13(obj)
    if isinstance(obj, dict):
        return {k: _round_floats_wire(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_round_floats_wire(v) for v in obj]
    return obj


def _try_get_active_sw() -> Any | None:
    """Return a wrapped ``SldWorks.Application`` if one is running, else ``None``.

    Uses ``win32com.client.GetActiveObject``, which calls
    ``pythoncom.GetActiveObject`` then casts to ``IDispatch`` and wraps the
    result so callers see late-bound attribute access (``sw.GetAddInObject``,
    ``sw.LoadAddIn``, etc.). The bare ``pythoncom.GetActiveObject`` returns a
    raw ``PyIUnknown`` with no method surface — using that directly fails on
    every SW API call.
    """
    try:
        import win32com.client  # type: ignore[import-not-found]
    except ImportError:
        return None
    try:
        return win32com.client.GetActiveObject(_SW_PROGID)
    except Exception:
        return None


def running_objects() -> dict[str, Any]:
    """The Running Object Table by display name. Every SOLIDWORKS registers ``SolidWorks_PID_<pid>`` and,
    once fully started, ``SolidWorks_LaunchComplete_PID_<pid>``."""
    import pythoncom  # type: ignore[import-not-found]
    with contextlib.suppress(pythoncom.com_error):  # the table needs COM on the calling thread
        pythoncom.CoInitialize()
    rot, ctx = pythoncom.GetRunningObjectTable(), pythoncom.CreateBindCtx(0)
    return {moniker.GetDisplayName(ctx, None): (rot, moniker) for moniker in rot}


def _sw_for_pid(pid: int) -> Any | None:
    """The ``SldWorks.Application`` of one specific instance, late-bound like ``_try_get_active_sw``."""
    import pythoncom  # type: ignore[import-not-found]
    import win32com.client.dynamic  # type: ignore[import-not-found]
    entry = running_objects().get(f"SolidWorks_PID_{pid}")
    if entry is None:
        return None
    rot, moniker = entry
    return win32com.client.dynamic.Dispatch(rot.GetObject(moniker).QueryInterface(pythoncom.IID_IDispatch))


def _com_state(sw: Any) -> str:
    """"ok", or the COM error a direct call on ``sw`` raises: late binding reports any as a missing attribute."""
    import pythoncom  # type: ignore[import-not-found]
    try:
        sw._oleobj_.GetIDsOfNames(0, "GetAddInObject")
        return "ok"
    except pythoncom.com_error as error:
        return f"{error.hresult & 0xFFFFFFFF:#010x} {error.strerror}"


def _try_get_addin(sw: Any, progid: str) -> Any | None:
    """Best-effort `sw.GetAddInObject(progid)` — returns None on COM throw.

    SW raises HRESULT errors when the addin manager isn't ready or the addin
    isn't loaded yet. We treat both the throw case and the returned-None case
    the same (no addin yet) so the caller can fall through to LoadAddIn.
    """
    try:
        return sw.GetAddInObject(progid)
    except Exception:
        return None


class ComTransport:
    """Synchronous COM dispatcher over the in-process SolidWorks plugin.

    Attaches to the active SolidWorks, or with ``pid`` to that instance; SolidWorks must already be running.

    Once attached, fetches the addin instance via ``sw.GetAddInObject(addin_progid)``,
    falling back to ``sw.LoadAddIn(addin_progid)`` if the addin isn't loaded.
    ``call(command, **kwargs)`` JSON-encodes kwargs (renamed to PascalCase wire
    keys) and calls ``RunCommand(command, params_json, timeout)`` on the addin.
    """

    def __init__(
        self,
        addin_progid: str = "Sldworks.Plugin",
        timeout_seconds: int = 600,
        pid: Optional[int] = None,
    ) -> None:
        try:
            import win32com.client  # type: ignore[import-not-found]  # noqa: F401
        except ImportError as e:  # pragma: no cover - install hint
            raise RuntimeError(
                "pywin32 is required for the COM transport. "
                "Install it with `pip install pywin32` (Windows only)."
            ) from e

        sw = _try_get_active_sw() if pid is None else _sw_for_pid(pid)
        if sw is None:
            raise RuntimeError("No SOLIDWORKS is running. Start SOLIDWORKS with the Dahlia add-in, then connect again."
                               if pid is None else f"No SOLIDWORKS with process id {pid} is registered with COM")

        # Make SW visible: nudges a freshly-booted instance to finish
        # initializing and matches what a human user would expect to see.
        try:
            sw.Visible = True
        except Exception:
            pass

        addin = _try_get_addin(sw, addin_progid)
        if addin is None:
            # SW didn't load the addin at startup (common cause: regasm ran as
            # admin so the HKCU\Software\SolidWorks\AddInsStartup entry landed
            # under the admin user's profile, not the running user's). Force a
            # load via ISldWorks.LoadAddIn — returns swLoadAddinError_e int
            # (0 = success) but we don't read it; just re-fetch and surface
            # whatever happened.
            load_error: Exception | None = None
            load_result: Any = None
            try:
                load_result = sw.LoadAddIn(addin_progid)
            except Exception as e:  # noqa: BLE001 — caught and reported below
                load_error = e
            addin = _try_get_addin(sw, addin_progid)
            if addin is None:
                msg = (
                    f"Could not load addin {addin_progid!r}. "
                    "Check Tools > Add-Ins in SolidWorks."
                )
                if load_error is not None:
                    msg += f" sw.LoadAddIn(..) raised: {load_error}"
                else:
                    # swLoadAddinError_e: 0 success, 1 NotFound, 2 NotPermitted,
                    # 3 IncompatibleVersion, 4 Error, 5 LoadOnlyAtStartup.
                    msg += f" sw.LoadAddIn(..) returned {load_result!r} (swLoadAddinError_e)."
                raise RuntimeError(f"{msg} SolidWorks itself answers: {_com_state(sw)}")

        self._sw = sw
        self._addin = addin
        self._timeout = int(timeout_seconds)
        # Late-bound pywin32 invokes zero-argument COM methods on attribute access.
        self._pid = int(sw.GetProcessID)

    @property
    def application(self) -> Any:
        """The ``SldWorks.Application`` COM object, for code that needs the raw API."""
        return self._sw

    @property
    def addin(self) -> Any:
        return self._addin

    @property
    def pid(self) -> int:
        return self._pid

    def call(self, command: str, **kwargs: Any) -> Any:
        params = {_wire_key(k): v for k, v in kwargs.items()}
        # Floats are rounded to 13 significant figures, so a call and a printed script that
        # repeats it send byte-identical values.
        plain = _round_floats_wire(json.loads(json.dumps(params, default=_json_default)))
        params_json = json.dumps(plain)
        raw = self._addin.RunCommand(command, params_json, self._timeout)
        result = json.loads(raw)
        if isinstance(result, dict) and "Error" in result:
            raise PluginError(f"{command}: {result['Error']}")
        return result
