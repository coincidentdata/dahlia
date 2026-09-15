# Working with Dahlia

Dahlia is a Python client for a native CAD add-in. Read [README.md](README.md)
for installation and [docs/usage.md](docs/usage.md) before writing CAD scripts.
All commands below run from this repository's root.

## Setup and checks

- Use Python 3.12. Run `uv sync --locked` to install the client and development dependencies.
- Run `uv run pytest` for the Python unit tests and `uv build` to check packaging.
- Imports, feature construction, and unit tests work without SOLIDWORKS.
- Live CAD operations require Windows x64, SOLIDWORKS, and the matching Dahlia
  add-in. Install the native add-in separately as described in
  [installer/README.md](installer/README.md); Python installation does not install it.
- Check connectivity with
  `uv run python -c "import dahlia; dahlia.connect(); print([f.name for f in dahlia.list_files()])"`.
  Report missing prerequisites rather than treating a mocked run as native verification.

## Writing scripts

- Import public factories and constants from `dahlia`. Start with
  [examples/quickstart.py](examples/quickstart.py); the
  [turbofan demo](examples/turbofan_demo.py) covers a complete assembly.
- Find the complete public API in [src/dahlia/__init__.py](src/dahlia/__init__.py).
  Feature and mate modules define their factory parameters and typed options.
- Call `connect()` before operations that talk to CAD.
- All lengths are meters and all angles radians. Use `MM`, `INCH`, and `DEG`
  multipliers. `set_units()` changes display units only.
- Add a sketch before adding features that reference it. Use the returned names
  and inspected geometry definitions; do not invent native entity IDs.
- Use `view()`, `inspect()`, `probe()`, and `get_image()` to check the actual result.
  An exported image alone does not prove that features or mates are healthy.
- Save new documents with unique native filenames. Keep an assembly and its
  referenced part files together. Put generated CAD, images, and reports in `outputs/`.
- Run only one live CAD script at a time. Some native commands use SOLIDWORKS UI;
  leave the app available and finish any active PropertyManager operation first.
- Preserve unrelated documents. Close only documents your script owns, and do
  not use `close_all_files()` as general cleanup.
- Surface errors. A failed operation may have changed the model; inspect it
  before retrying. Do not claim a live check passed unless it ran successfully.

## Editing the client

- Public exports: [src/dahlia/__init__.py](src/dahlia/__init__.py).
- Document operations: `src/dahlia/file.py`; connection and transport:
  `src/dahlia/session.py` and `src/dahlia/transport.py`.
- Feature models/factories: [src/dahlia/features/](src/dahlia/features/);
  sketch builders: [src/dahlia/sketch/](src/dahlia/sketch/);
  mate models/factories: [src/dahlia/mates/](src/dahlia/mates/).
- Keep public methods and validation consistent with the existing typed models.
  The native backend determines which operations can actually execute.
- Installer packaging lives in `installer/` and consumes compiled payloads.
  Keep executables, DLLs, debug symbols, recordings, and generated models out of commits.
