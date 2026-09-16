# SOLIDWORKS backend

The C# add-in receives JSON commands from the Python client and runs them on
SOLIDWORKS' application thread. The core implements document operations,
sketches, features, assemblies, mates, and geometric references.

- [core/](core/): native CAD implementation.
- [plugin/](plugin/): COM registration, command queue, and dispatch.
- [build.ps1](build.ps1): builds the native payload and collects its dependencies.

The source uses the [Dahlia Community License](../LICENSE), like the Python client.
The prebuilt installer remains the easiest way to use Dahlia.

## Build from source

Use Windows x64 with the .NET 8 SDK and SOLIDWORKS 2026 installed. Install the
type-library exporter once:

```powershell
dotnet tool install --global dscom --version 2.1.0
```

From this repository's root, pass the `api\redist` directory from your SOLIDWORKS
installation. For a typical installation:

```powershell
.\solidworks\build.ps1 -SolidWorksInteropPath 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist'
```

Adjust the path for your installation. SOLIDWORKS provides the
`SolidWorks.Interop.sldworks.dll`, `swcommands.dll`, `swconst.dll`, and
`swpublished.dll` assemblies in that directory, each with the
`SolidWorks.Interop.` prefix. These vendor assemblies are not checked into this
repository. See the [SOLIDWORKS API getting-started guide](https://3dswym.3dexperience.3ds.com/wiki/solidworks-news-info/getting-started-with-the-solidworks-api-solidpractices_eLpSxUm2THqG6jwQdSrMGQ).

The script restores NuGet dependencies and writes `solidworks/obj/payload/`,
including the core, add-in, COM host, type library, dependencies, and notices.
Versions come from `pyproject.toml`. Use `-Configuration Debug` for a debug build.
The build requires no administrator privileges and does not stop SOLIDWORKS,
install the add-in, or change COM registration.

For compilation alone, an IDE or command line can build the plugin project:

```powershell
dotnet build .\solidworks\plugin\SldworksPlugin.csproj -c Release '-p:SolidWorksInteropPath=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist'
```

## Install your build

Install Inno Setup 7.1.0, then package the native payload:

```powershell
.\installer\build.ps1 -PayloadDirectory .\solidworks\obj\payload -IsccPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

The installer and checksum appear in `dist/`. Save your work and close SOLIDWORKS
before running the installer. Enable **Dahlia for SOLIDWORKS** in **Tools > Add-Ins**,
then use the matching Python client. See [installer details](../installer/README.md)
for prerequisites, unattended installation, and troubleshooting.

## Implementation conventions

Each feature handler owns creation and inspection for its wire type. Mate handlers
share a lifecycle in `core/Handlers/Mates/MateHandler.cs`. Geometry references are
captured and resolved through `core/Definitions/`.

Keep the JSON wire format compatible with the Python models. Lengths are meters
and angles are radians. Pair feature-data selection access with release in a
`finally` block, and report unsupported input or native failures as errors.
SOLIDWORKS operations run through the add-in's application-thread dispatcher;
UI-driven operations depend on the application remaining available.

Python tests and installer checks are included in this repository. Maintainers
run CAD integration and end-to-end tests separately. Describe the SOLIDWORKS
version and native behavior you verified when contributing a change.
