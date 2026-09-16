using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Shared;

// Shared feature-scope inspect capture for every handler that exposes a
// `feature_scope` wire field (Extrude, Revolve, Sweep, Loft, Cut* variants).
//
// SW quirk: on a single-body part, `FeatureScopeBodies` is non-empty when SW
// reloaded the .sldprt from disk (legacy explicit-scope storage) but empty
// after a fresh `CreateFeature` / `InsertProtrusionBlend2` (SW collapses the
// redundant scope). feature_scope carries no information for a 1-body part —
// there's nothing else to scope into — so we normalize to [] in both cases.
// This keeps the wire format stable across save/reload and across our
// re-bake, and removes a class of spurious round-trip diffs.
internal static class FeatureScope {
    // `scopedBodies` is the handler's `data.FeatureScopeBodies` (typed as
    // `Object` in the SW interop, comes back as `object[]` or null). Pass it
    // through directly.
    public static JsonArray Capture(File file, object? scopedBodies) {
        var arr = new JsonArray();
        if (CountSolidBodies(file) <= 1) return arr;
        if (scopedBodies is not object[] items) return arr;
        foreach (Body2 body in items) {
            if (body is null) continue;
            arr.Add(DefinitionCapture.Capture(body).ToJson());
        }
        return arr;
    }

    private static int CountSolidBodies(File file) {
        var raw = ((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        return raw?.Length ?? 0;
    }

    // Edit-side companion of Capture: resolve a wire `feature_scope` (the handler's
    // XxxArgs.FeatureScope) into the SAFEARRAY-of-IDispatch payload the
    // IXxxFeatureData(2).set_FeatureScopeBodies setters want.
    //
    // null/empty => null (the caller flips data.AutoSelect=true; on a 1-body part scope is
    // ALWAYS empty per Capture's ≤1-body normalization). Non-empty => each body Definition
    // is resolved to its live entity and wrapped in a DispatchWrapper, matching the other
    // SAFEARRAY feature-data selection setters (a bare object[] crashes the marshaller).
    public static object? ResolveScopeBodies(File file, IReadOnlyList<Definition>? scope) {
        if (scope is null || scope.Count == 0) return null;
        var live = scope.Select(d => DefinitionResolver.Resolve(file, d)
            ?? throw new InvalidOperationException(
                $"FeatureScope: body ref ({d.GetType().Name}) did not resolve to a live entity")).ToArray();
        return live.Select(o => (object)new DispatchWrapper(o)).ToArray();
    }
}
