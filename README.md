# Dahlia

<img src="https://raw.githubusercontent.com/coincidentdata/dahlia/main/assets/dahlia-logo.png" alt="Dahlia single-flower logo" width="160">

Our Python library for interacting with industry CAD software, starting with SOLIDWORKS.

Create and edit parts, assemble components with mates, inspect geometry, and
export native CAD files, STEP, STL, and images from Python.

[Installation](#installation) · [Usage guide](https://github.com/coincidentdata/dahlia/blob/main/docs/usage.md) · [Examples](https://github.com/coincidentdata/dahlia/tree/main/examples) · [Agent instructions](https://github.com/coincidentdata/dahlia/blob/main/AGENTS.md)

## Installation

Live CAD operations require **Windows x64, SOLIDWORKS, Python 3.12 x64, and
the Dahlia for SOLIDWORKS add-in**. The current installer targets SOLIDWORKS
2026; other versions have not been verified.

### 1. Install the SOLIDWORKS add-in

Save your work and close SOLIDWORKS, then run the matching
`Dahlia-SOLIDWORKS-<version>-x64-Setup.exe`. It installs the add-in, core
library, and required .NET Desktop Runtime. It does not install SOLIDWORKS
or Python, and you do not need the .NET SDK.

Start SOLIDWORKS and enable **Dahlia for SOLIDWORKS** under **Tools > Add-Ins**.
Enable its startup checkbox to load it automatically.

Download the installer and SHA-256 file from the
[v0.1.1 preview release](https://github.com/coincidentdata/dahlia/releases/tag/v0.1.1).
See [installer details](https://github.com/coincidentdata/dahlia/blob/main/installer/README.md) for preview status, requirements,
silent installation, and troubleshooting.

### 2. Install the Python client

In a Python 3.12 environment:

```powershell
python -m pip install dahlia-cad
```

Or add it to a project with [uv](https://docs.astral.sh/uv/):

```powershell
uv add dahlia-cad
```

The client can be imported and its unit tests run without SOLIDWORKS.
Creating or editing CAD requires the live add-in.

### 3. Check the connection

With SOLIDWORKS open and the add-in enabled:

```powershell
python -c "import dahlia; dahlia.connect(); print([f.name for f in dahlia.list_files()])"
```

An empty list is normal when no documents are open. A connection error means
the native setup needs attention; see [troubleshooting](https://github.com/coincidentdata/dahlia/blob/main/installer/README.md#troubleshooting).

## First part

```python
from dahlia import MM, TOP, connect, create_file, extrude, sketch

connect()
part = create_file()
profile = sketch(plane=TOP, name="SpacerProfile")
profile.add_circle((0, 0), radius=12 * MM)
profile.add_circle((0, 0), radius=5 * MM)
part.add_feature(profile)
part.add_feature(extrude(sketch=profile, depth=8 * MM, name="Spacer"))
print(part.view())
```

Lengths are **meters** and angles are **radians**. Use `MM`, `INCH`, and
`DEG` as multipliers. Document display units do not change these API units.

Download [quickstart.py](https://raw.githubusercontent.com/coincidentdata/dahlia/main/examples/quickstart.py)
for a complete script that also saves a native part, STEP file, and PNG:

```powershell
python quickstart.py
```

Each run writes to a new directory under `outputs/quickstart/` and uses a
unique document name.

## Development and agents

From a checkout, start with [AGENTS.md](https://github.com/coincidentdata/dahlia/blob/main/AGENTS.md).
API conventions, geometry references, assemblies, and supported-feature limitations
are in the [usage guide](https://github.com/coincidentdata/dahlia/blob/main/docs/usage.md).
The C# SOLIDWORKS core and add-in are in
[solidworks/](https://github.com/coincidentdata/dahlia/tree/main/solidworks), with
[source build instructions](https://github.com/coincidentdata/dahlia/blob/main/solidworks/README.md).
For pull requests and the review process, see [CONTRIBUTING.md](https://github.com/coincidentdata/dahlia/blob/main/CONTRIBUTING.md).

```powershell
uv sync --locked
uv run pytest
uv build
```

The tests validate the Python client without a SOLIDWORKS session; they do not
replace live CAD verification.

## License

[Dahlia Community License 1.0](https://github.com/coincidentdata/dahlia/blob/main/LICENSE) permits free use, including commercial
use, unless you and your affiliates have more than **USD 10 million in combined
revenue over the preceding twelve months**. Above that threshold, a separate
commercial license agreement is required before use, including internal use.
Contact [hello@coincidentdata.com](mailto:hello@coincidentdata.com).

The Python client, C# SOLIDWORKS add-in, and core are source-available under
the same license. Prebuilt Windows installers are also provided.
