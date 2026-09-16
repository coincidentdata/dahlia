# SOLIDWORKS add-in

A COM wrapper over the [core](../core/). `Main.cs` contains registration,
connection lifecycle, the application-thread command queue, and dispatch.
See [the backend guide](../README.md) to build and install it.

The Python client calls `RunCommand(command, parameters, timeoutSeconds)` with
JSON parameters. The add-in queues CAD work on SOLIDWORKS' application thread,
dispatches it to the core, and serializes the result or error.

The registered ProgID is `Sldworks.Plugin`; the add-in appears as
**Dahlia for SOLIDWORKS**. Preserve its COM identity and wire format so existing
clients and installations remain compatible.
