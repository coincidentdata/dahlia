# Dahlia for SOLIDWORKS installer

The Windows x64 installer contains the compiled add-in, core library,
dependencies, type library, and Microsoft .NET 8 Desktop Runtime.
SOLIDWORKS and Python are installed separately. SOLIDWORKS 2026 is the
currently verified target.

The add-in implementation is distributed as binaries. This directory contains
the installer packaging and checks; it does not contain the C# implementation.
The [Dahlia Community License](../LICENSE) is shown during installation and
installed as `LICENSE.txt`. Third-party components retain their own notices.

## Install

Use the installer matching your Dahlia client version. Download the installer,
SHA-256 file, and Python packages from the
[v0.1.1 preview release](https://github.com/coincidentdata/dahlia/releases/tag/v0.1.1).
Executables are distributed as release assets.

The installer is an unsigned preview. Installation, runtime setup, reinstallation,
startup-preference preservation, and uninstall have been checked on the development
machine. Fresh-machine and older-release upgrade verification are still pending.

1. Save your work and close SOLIDWORKS.
2. Run `Dahlia-SOLIDWORKS-<version>-x64-Setup.exe` and approve the Windows
   administrator prompt.
3. Start SOLIDWORKS and enable **Dahlia for SOLIDWORKS** in **Tools > Add-Ins**.
4. Install the Python client and check connectivity as described in the
   [main README](../README.md#installation).

Setup installs or updates .NET Desktop Runtime x64 if needed. A .NET 9 or 10
runtime alone does not replace the required .NET 8 runtime. If Windows requires
a restart, restart and rerun Setup to finish installing the add-in.

Files are installed under `Program Files\Dahlia\SOLIDWORKS`. Setup never
terminates SOLIDWORKS automatically. Upgrades preserve an existing startup
setting and reject downgrades. Uninstall through Windows Settings > Apps;
SOLIDWORKS must be closed, and the shared Microsoft runtime is retained.

Read the license before explicitly accepting it for unattended installation:

```powershell
.\Dahlia-SOLIDWORKS-0.1.1-x64-Setup.exe /VERYSILENT /ACCEPTLICENSE=1 /SUPPRESSMSGBOXES /NORESTART /LOG="install.log"
```

## Troubleshooting

- **SOLIDWORKS is running:** save your work, close it, and rerun Setup.
- **Add-in missing or connection fails:** verify the installer completed, start
  SOLIDWORKS, and enable the add-in in Tools > Add-Ins. Run the connection check
  from the same Python environment used for your scripts.
- **Add-in startup differs between Windows accounts:** enable the startup
  checkbox while signed in as the account that will run SOLIDWORKS.
- **Python import fails:** use Python 3.12 and install this checkout in the
  environment running the script. `uv run` uses the repository environment.
- **Runtime installation fails:** inspect the installer log. Setup reports
  prerequisite errors and stops before registering the plugin.

## Build an installer from compiled binaries

Packaging requires Windows and Inno Setup 7.1.0. It does not need the .NET SDK,
a C# checkout, or a running SOLIDWORKS session.

Supply a prepared payload containing `SldworksPlugin.dll`,
`SldworksCore.dll`, `SldworksPlugin.comhost.dll`, `SldworksPlugin.tlb`,
the plugin's `.deps.json` and `.runtimeconfig.json`, all runtime dependencies,
and their license notices. The payload must match the version in
[pyproject.toml](../pyproject.toml).

From this repository's root:

```powershell
.\installer\build.ps1 -PayloadDirectory C:\build\dahlia-payload -IsccPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

The build verifies required files, downloads the Microsoft runtime pinned in
`prerequisites.json`, and checks its SHA-512 hash and Microsoft signature.
The runtime is cached in `installer/obj/`. The output installer and SHA-256
sidecar go to `dist/`; all generated files are ignored by source control.
Packaging does not install the add-in or change registration.

## Checks

The prerequisite check reads runtime versions and process state without
installing anything:

```powershell
ISCC.exe /DRuntimeVersion=8.0.31 installer/tests/prerequisites.iss
.\installer\obj\tests\prerequisites-test.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG="prerequisites.log"
```

A passing log contains `DAHLIA_PREREQUISITES_PASSED`. The executable deliberately
returns a nonzero exit code because it exits before installation.

With a prepared payload, this check exercises the real COM host's registration
callbacks in isolated temporary registry keys and verifies live registration
is unchanged:

```powershell
uv run python installer/tests/registration.py C:\build\dahlia-payload
```

It covers repeated registration/unregistration and type-library loading.
A fresh elevated installation, upgrade, and uninstall still require separate
verification on a Windows machine with SOLIDWORKS. Sign release executables
before public distribution.
