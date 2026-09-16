# SOLIDWORKS core

The native CAD implementation, called by the [COM add-in](../plugin/).
See [the backend guide](../README.md) for build instructions and conventions.

- `Session.cs`: document creation, opening, and lookup.
- `File.cs` and `File.*.cs`: document, geometry, appearance, assembly, and movement operations.
- `Definitions/`: capture geometric references during inspection and resolve them during creation.
- `Handlers/`: feature creation and inspection, with shared helpers in `Handlers/Shared/`.
- `Handlers/Mates/`: a shared mate lifecycle and one handler per supported mate type.
- `Handlers/Sketch/`: sketch entities, dimensions, relations, and composite operations.
- `utils/`: geometry math, JSON handling, native controls, and selection helpers.
- `Flags.cs` and `SldworksLog.cs`: tolerances and logging.

The handler dispatch tables and Python factories define the supported wire types.
Native entities are resolved from their definitions for each operation. Pair
`AccessSelections` with `ReleaseSelectionAccess` in a `finally` block, and report
unsupported input or native failures instead of silently continuing.
