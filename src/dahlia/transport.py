"""COM transport — pywin32 dispatcher into the in-process SolidWorks plugin.

Wire conventions:
  - Top-level command-arg keys are PascalCase (FileName, Index, Feature, ...).
  - Payload contents (inside Feature, Ray, etc.) stay snake_case.
  - Errors arrive as {"Error": "<message>"} from the plugin's outer try/catch.

This module is import-safe on non-Windows (pywin32 is lazy-imported inside
``ComTransport.__init__``).
"""
from __future__ import annotations

import json
import logging
import os
import subprocess
import time
from typing import Any

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


# Registry locations for SolidWorks / 3DEXPERIENCE launch.
_SWX_LAUNCHER_SUBKEY = r"SOFTWARE\Dassault Systemes\SWXConnectors\SWXDesktopLauncher"
_SLDWORKS_CLSID_SUBKEY = (
    r"SOFTWARE\Classes\CLSID\{83A33D30-27C5-11CE-BFD4-00400513BB57}\LocalServer32"
)
_3DEX_SUBKEY = r"Software\Dassault Systemes\SolidWorksPDM\Servers\3DEXPERIENCE"
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


def _read_registry_value(hive_name: str, subkey: str, value_name: str) -> str | None:
    """Read a single registry value, returning ``None`` if missing.

    ``hive_name`` is one of ``"HKLM"`` or ``"HKCU"``. Empty strings are
    returned as ``None`` so callers can treat "missing" and "blank" alike.
    """
    try:
        import winreg  # type: ignore[import-not-found]
    except ImportError:
        return None
    hive = winreg.HKEY_LOCAL_MACHINE if hive_name == "HKLM" else winreg.HKEY_CURRENT_USER
    try:
        with winreg.OpenKey(hive, subkey) as key:
            value, _ = winreg.QueryValueEx(key, value_name)
    except FileNotFoundError:
        return None
    except OSError:
        return None
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _clear_3dex_username() -> None:
    """Defensively clear ``HKCU\\...\\3DEXPERIENCE\\UserName`` before launch.

    Failures are logged and swallowed because this is a hint, not a hard
    requirement.
    """
    try:
        import winreg  # type: ignore[import-not-found]
    except ImportError:
        return
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, _3DEX_SUBKEY, 0, winreg.KEY_SET_VALUE
        ) as key:
            winreg.SetValueEx(key, "UserName", 0, winreg.REG_SZ, "")
            logger.info("Cleared HKCU 3DEXPERIENCE UserName before launch")
    except Exception as exc:  # noqa: BLE001 — defensive log-and-continue
        logger.warning("Failed to clear 3DEXPERIENCE UserName: %s", exc)


def _parse_local_server_path(raw: str) -> str:
    """Extract the .exe path from a ``LocalServer32`` registry value.

    The value commonly looks like ``"C:\\...\\SLDWORKS.exe" /RegServer`` or
    ``C:\\...\\SLDWORKS.exe /something``. We strip a leading quoted token if
    present, otherwise take everything before the first space.
    """
    if raw.startswith('"'):
        end_quote = raw.find('"', 1)
        if end_quote > 0:
            return raw[1:end_quote]
        return raw[1:].rstrip('"')
    space = raw.find(" ")
    return raw[:space] if space > 0 else raw


def _launch_standalone() -> None:
    """Cold-start a standalone SolidWorks install via its registered exe."""
    raw = _read_registry_value("HKLM", _SLDWORKS_CLSID_SUBKEY, "")
    if not raw:
        raise RuntimeError(
            "SolidWorks executable path not found in registry "
            f"(HKLM\\{_SLDWORKS_CLSID_SUBKEY}). "
            "Is SolidWorks installed?"
        )
    exe_path = _parse_local_server_path(raw)
    if not os.path.isfile(exe_path):
        raise RuntimeError(
            f"SolidWorks executable not found at path: {exe_path!r} "
            f"(parsed from HKLM\\{_SLDWORKS_CLSID_SUBKEY})."
        )
    logger.info("Starting standalone SolidWorks from: %s", exe_path)
    subprocess.Popen(  # noqa: S603 — exe path is trusted (read from HKLM)
        [exe_path],
        cwd=os.path.dirname(exe_path) or None,
        close_fds=True,
    )


def _launch_makers(launcher_dir: str) -> None:
    """Cold-start a Makers / 3DEXPERIENCE-connected SolidWorks via CATSTART.exe.

    Reads the four required 3DEX values from HKCU and assembles the launch
    args. If any required value is missing, raises ``RuntimeError`` rather
    than launching with empty args.
    """
    catstart = os.path.join(launcher_dir, "CATSTART.exe")
    if not os.path.isfile(catstart):
        raise RuntimeError(
            f"CATSTART.exe not found at {catstart!r}. "
            "SWXDesktopLauncher registry path is set but the launcher binary "
            "is missing — reinstall SolidWorks Makers / 3DEXPERIENCE."
        )

    tenant_id = _read_registry_value("HKCU", _3DEX_SUBKEY, "TenantId")
    space_url = _read_registry_value("HKCU", _3DEX_SUBKEY, "RUNNING_SpaceURL") or \
        _read_registry_value("HKCU", _3DEX_SUBKEY, "SpaceURL")
    my_apps_url = _read_registry_value("HKCU", _3DEX_SUBKEY, "MyAppsURL")
    registry_url = _read_registry_value("HKCU", _3DEX_SUBKEY, "RUNNING_RegistryURL") or \
        _read_registry_value("HKCU", _3DEX_SUBKEY, "RegistryURL")

    missing = [
        name for name, value in (
            ("TenantId", tenant_id),
            ("SpaceURL (or RUNNING_SpaceURL)", space_url),
            ("MyAppsURL", my_apps_url),
            ("RegistryURL (or RUNNING_RegistryURL)", registry_url),
        ) if not value
    ]
    if missing:
        raise RuntimeError(
            "Missing required 3DEXPERIENCE registry value(s) under "
            f"HKCU\\{_3DEX_SUBKEY}: {', '.join(missing)}. "
            "Sign in to SolidWorks Makers / 3DEXPERIENCE at least once so "
            "the launcher writes these values."
        )

    _clear_3dex_username()

    # The inner -object payload is a single quoted token; the embedded
    # \"SWXCSWK_AP\" stays escaped because CATSTART parses it as a sub-arg.
    object_arg = (
        f'-Url={space_url} --AppName=\\"SWXCSWK_AP\\" '
        f'-MyAppsURL={my_apps_url} -tenant={tenant_id} -monoapp '
        f'-RegistryUrl={registry_url} -3DRegistryURL={registry_url}'
    )
    arguments = (
        f'-run SWXDesktopLauncher.exe -object "{object_arg}" -nowindow'
    )

    # CATSTART parses its own command line; pass it as a single string via
    # the shell to preserve the inner quoting exactly.
    command_line = f'"{catstart}" {arguments}'
    logger.info("Launching SolidWorks (Makers/3DEXPERIENCE) via: %s", command_line)
    subprocess.Popen(  # noqa: S602 — args are read from HKLM/HKCU, not user input
        command_line,
        shell=True,
        cwd=launcher_dir,
        close_fds=True,
    )


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


def _wait_for_sw(max_attempts: int, delay_seconds: float) -> Any:
    """Poll ``GetActiveObject`` until SW is reachable or the budget expires."""
    for attempt in range(1, max_attempts + 1):
        time.sleep(delay_seconds)
        sw = _try_get_active_sw()
        if sw is not None:
            logger.info(
                "Attached to SolidWorks COM after %.0fs (attempt %d/%d)",
                attempt * delay_seconds, attempt, max_attempts,
            )
            return sw
        logger.debug(
            "Attempt %d/%d: SolidWorks COM not yet available", attempt, max_attempts,
        )
    total = max_attempts * delay_seconds
    raise RuntimeError(
        f"Failed to connect to SolidWorks after {total:.0f}s "
        f"({max_attempts} attempts × {delay_seconds:.0f}s). "
        "The launcher started but the COM server never registered — check "
        "for a login dialog or splash screen waiting for input."
    )


def _attach_or_launch_sw(max_attempts: int, delay_seconds: float) -> Any:
    """Two-prong launch logic: attach if running, else cold-start + poll."""
    sw = _try_get_active_sw()
    if sw is not None:
        logger.debug("Attached to existing SolidWorks COM instance")
        return sw

    launcher_dir = _read_registry_value("HKLM", _SWX_LAUNCHER_SUBKEY, "Path")
    if launcher_dir:
        logger.info(
            "SWXDesktopLauncher detected at %s — using Makers/3DEXPERIENCE launch",
            launcher_dir,
        )
        _launch_makers(launcher_dir)
    else:
        logger.info(
            "SWXDesktopLauncher not in registry — using standalone launch",
        )
        _launch_standalone()

    return _wait_for_sw(max_attempts=max_attempts, delay_seconds=delay_seconds)


class ComTransport:
    """Synchronous COM dispatcher over the in-process SolidWorks plugin.

    On construction, attempts (in order):
      1. Attach to a running SolidWorks via ``GetActiveObject``.
      2. If none, detect the install kind via the SWXDesktopLauncher registry
         key. Makers / 3DEXPERIENCE installs are launched with ``CATSTART.exe``
         + 3DEX args; standard installs are launched via the ``LocalServer32``
         exe path.
      3. Poll ``GetActiveObject`` until SW is reachable (or the budget expires).

    Once attached, fetches the addin instance via ``sw.GetAddInObject(addin_progid)``,
    falling back to ``sw.LoadAddIn(addin_progid)`` if the addin isn't loaded.
    ``call(command, **kwargs)`` JSON-encodes kwargs (renamed to PascalCase wire
    keys) and calls ``RunCommand(command, params_json, timeout)`` on the addin.
    """

    def __init__(
        self,
        addin_progid: str = "Sldworks.Plugin",
        timeout_seconds: int = 600,
        launch_max_attempts: int = 30,
        launch_delay_seconds: float = 5.0,
    ) -> None:
        try:
            import win32com.client  # type: ignore[import-not-found]  # noqa: F401
        except ImportError as e:  # pragma: no cover - install hint
            raise RuntimeError(
                "pywin32 is required for the COM transport. "
                "Install it with `pip install pywin32` (Windows only)."
            ) from e

        sw = _attach_or_launch_sw(
            max_attempts=launch_max_attempts,
            delay_seconds=launch_delay_seconds,
        )

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
                raise RuntimeError(msg)

        self._sw = sw
        self._addin = addin
        self._timeout = int(timeout_seconds)

    def call(self, command: str, **kwargs: Any) -> Any:
        params = {_wire_key(k): v for k, v in kwargs.items()}
        # Round every float to the same 13 sig figs the transcript emitter uses, so the
        # runner (RTT) builds with byte-identical values to what verify replays. Serialize
        # via _json_default first (resolves models to plain dicts), then round, then dump.
        plain = _round_floats_wire(json.loads(json.dumps(params, default=_json_default)))
        params_json = json.dumps(plain)
        raw = self._addin.RunCommand(command, params_json, self._timeout)
        result = json.loads(raw)
        if isinstance(result, dict) and "Error" in result:
            raise PluginError(f"{command}: {result['Error']}")
        return result
