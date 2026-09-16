using System.Text.Json.Nodes;

namespace Sldworks.Core.Definitions;

// Canonical Definition <-> Definition comparison. Two flavors:
//
//   Exact   — both Defs come from the SAME source state (e.g. GenerateProbe's
//             source-verify loop: live Capture vs ray-hit Capture). Identical
//             entities bit-match within float noise; compare every field with
//             Flags.GeometryTolerance.
//
//   Approx  — Defs come from DIFFERENT states (source rolled-back vs target
//             after Add). Sample fields like `on_face_point` are UV witnesses
//             that legitimately differ across captures of the same face;
//             skip them. Identity fields (normal/axis/radius/parent_body/...)
//             compare with a wider tolerance for legitimate mass-props drift.
//
// Both forms require type equality (PlanarFaceDefinition can never approx-match
// a CylindricalFaceDefinition — same as Exact). Inside that, sample-field
// skipping is per-kind; everything else compares with tolerance.
public static class DefinitionMatch {
    // Tolerance for analytic geometry fields (Direction components, radii,
    // start/end points) in Approx mode. 1e-4 m = 0.1 mm — looser than
    // GeometryTolerance (1e-7) so source/target capture noise doesn't trip
    // it, tighter than 1 mm so a "wrong face" (different normal or radius)
    // still fails.
    public const double GeomApproxTol = 1e-4;

    // Tolerance for parent_body's mass-props fields (centroid, volume, bbox,
    // surface_area) in Approx mode. Body identity is structural; mass-props
    // drift across rebuilds is real (Parasolid tessellation non-determinism).
    // Use the existing BodyMassPropertyTol so this lines up with the
    // resolver's own face-by-body filter.
    public static double BodyApproxTol => Flags.BodyMassPropertyTol;

    public static bool Exact(Definition a, Definition b) {
        if (a.GetType() != b.GetType()) return false;
        return JsonNodesEqualWithinTol(a.ToJson(), b.ToJson(), Flags.GeometryTolerance);
    }

    public static bool Approx(Definition a, Definition b) {
        if (a.GetType() != b.GetType()) return false;
        if (a is ComponentEntityDefinition left && b is ComponentEntityDefinition right)
            return left.Component.SequenceEqual(right.Component) && Approx(left.Entity, right.Entity);
        var aJson = a.ToJson() as JsonObject;
        var bJson = b.ToJson() as JsonObject;
        if (aJson is null || bJson is null) return false;
        if (ApproxFieldByField(aJson, bJson, a.GetType())) return true;
        // SW quirk: an edge's parametric direction is non-deterministic
        // across rebuilds — the same physical edge can come back traversed
        // in opposite order. For each edge kind, try Approx-matching the
        // captured side with its direction reversed:
        //   line/circular/elliptical: swap (start, end)
        //   spline:                   reverse control_points + mirror knots
        if (HasParametricDirection(a.GetType())) {
            var bReversed = ReverseEdgeDirection(bJson);
            if (bReversed is not null
                    && ApproxFieldByField(aJson, bReversed, a.GetType())) {
                return true;
            }
        }
        return false;
    }

    // Edge Definitions whose identity is direction-agnostic. SW chooses edge
    // parametric direction non-deterministically on rebuild, so the captured
    // Def can come back representing the same curve traversed in reverse.
    // Matched by name suffix because the edge records don't share a common
    // base type — they all derive directly from Definition.
    private static bool HasParametricDirection(Type t) =>
        t.Name.EndsWith("EdgeDefinition", StringComparison.Ordinal);

    // Return a copy of the edge's JSON with its parametric direction
    // reversed, or null when no reverse transform applies. For start/end
    // edges (line/circular/elliptical) that's a simple swap. For
    // SplineEdge it's `reverse(control_points)` plus a knot mirror around
    // the parameter midpoint so the resulting curve parameterizes the same
    // shape backwards.
    private static JsonObject? ReverseEdgeDirection(JsonObject src) {
        if (src["start"] is JsonNode start && src["end"] is JsonNode end) {
            var swapped = new JsonObject();
            foreach (var kv in src) {
                swapped[kv.Key] = kv.Key switch {
                    "start" => end.DeepClone(),
                    "end"   => start.DeepClone(),
                    _ => kv.Value?.DeepClone(),
                };
            }
            return swapped;
        }
        if (src["control_points"] is JsonArray cps && src["knots"] is JsonArray knots
                && knots.Count >= 2) {
            // Mirror knot values around (knots[0] + knots[N-1]); reversed
            // array gives the same parametric span traversed backwards.
            var kFirst = knots[0]?.GetValue<double>() ?? 0.0;
            var kLast = knots[^1]?.GetValue<double>() ?? 0.0;
            var mirroredKnots = new JsonArray();
            for (var i = knots.Count - 1; i >= 0; i--) {
                var k = knots[i]?.GetValue<double>() ?? 0.0;
                mirroredKnots.Add(kFirst + kLast - k);
            }
            var reversedCps = new JsonArray();
            for (var i = cps.Count - 1; i >= 0; i--) {
                reversedCps.Add(cps[i]?.DeepClone());
            }
            var rebuilt = new JsonObject();
            foreach (var kv in src) {
                rebuilt[kv.Key] = kv.Key switch {
                    "control_points" => reversedCps,
                    "knots"          => mirroredKnots,
                    _ => kv.Value?.DeepClone(),
                };
            }
            return rebuilt;
        }
        return null;
    }

    private static bool ApproxFieldByField(JsonObject aJson, JsonObject bJson, Type type) {
        var sampleFields = SampleFieldsFor(type);
        var unorderedFields = UnorderedPointSetFieldsFor(type);
        foreach (var kv in aJson) {
            if (sampleFields.Contains(kv.Key)) continue;
            if (!bJson.TryGetPropertyValue(kv.Key, out var bv)) return false;
            if (unorderedFields.Contains(kv.Key)) {
                if (!PointSetsApproxEqual(kv.Value, bv, GeomApproxTol)) return false;
                continue;
            }
            var tol = kv.Key == "parent_body" ? BodyApproxTol : GeomApproxTol;
            if (!JsonNodesEqualWithinTol(kv.Value, bv, tol)) return false;
        }
        // Any keys in b not in a → also fail (must be same shape).
        foreach (var kv in bJson) {
            if (sampleFields.Contains(kv.Key)) continue;
            if (!aJson.ContainsKey(kv.Key)) return false;
        }
        return true;
    }

    // Fields whose value is a list of points whose order is non-identity.
    // BSurfaceFaceDefinition's `on_face_points` is a 3x3 UV-grid: SW chooses
    // U/V orientation non-deterministically on rebuild (U-flip, V-flip, and
    // U↔V swap all preserve the underlying set of 9 surface witness points),
    // so positional comparison spuriously fails. Resolver checks each point
    // lies on the face; mirror that here as set-equality.
    private static HashSet<string> UnorderedPointSetFieldsFor(Type t) {
        if (t == typeof(BSurfaceFaceDefinition)) return new() { "on_face_points" };
        return new();
    }

    // Bipartite point-set match: every point in A pairs with a distinct point
    // in B within tol, and vice versa. O(N²) but the inputs are tiny lists
    // (BSurfaceFaceDefinition's UV grid is 9 points).
    private static bool PointSetsApproxEqual(JsonNode? a, JsonNode? b, double tol) {
        if (a is not JsonArray aa || b is not JsonArray ba) return false;
        if (aa.Count != ba.Count) return false;
        var taken = new bool[ba.Count];
        for (var i = 0; i < aa.Count; i++) {
            var ai = aa[i];
            var matched = false;
            for (var j = 0; j < ba.Count; j++) {
                if (taken[j]) continue;
                if (JsonNodesEqualWithinTol(ai, ba[j], tol)) {
                    taken[j] = true;
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }

    // Sample (non-identity) field names by Definition subtype. SW captures these
    // as witness points/UV samples; the same live entity yields different values
    // across captures, so Approx skips them.
    //
    //   Analytic faces (planar/cyl/cone/sphere/torus): `on_face_point` is the
    //     UV-bbox midpoint snapped to trim — different surface state ⇒ different
    //     point on the same face.
    //   Cylinder/cone: `axis_origin`/`apex` is any point on the axis line. SW
    //     can return different points along the same axis across captures.
    //   Torus: `center` is identity (unique point). Don't skip.
    //   Sphere: `center` is identity (unique point). Don't skip.
    //
    // `axis_direction` is also a sample for surface/edge types whose axis is
    // a LINE rather than a directed vector — cylinder, torus, circular_edge,
    // elliptical_edge. SW captures the line's direction with non-deterministic
    // sign across rebuilds (the surface is symmetric about the axis line so
    // either sign is correct). Cone is excluded — its apex direction along
    // the axis is identity (the cone opens that way).
    //
    // BSurfaceFaceDefinition's `on_face_points` IS identity (the multi-sample
    // pattern discriminates between freeform faces) — don't skip.
    private static HashSet<string> SampleFieldsFor(Type t) {
        if (t == typeof(PlanarFaceDefinition)) return new() { "on_face_point" };
        if (t == typeof(SphericalFaceDefinition)) return new() { "on_face_point" };
        if (t == typeof(CylindricalFaceDefinition)) return new() { "on_face_point", "axis_origin", "axis_direction" };
        if (t == typeof(ConicalFaceDefinition)) return new() { "on_face_point", "apex" };
        if (t == typeof(ToroidalFaceDefinition)) return new() { "on_face_point", "axis_direction" };
        if (t == typeof(CircularEdgeDefinition)) return new() { "axis_direction" };
        if (t == typeof(EllipticalEdgeDefinition)) return new() { "axis_direction" };
        return new();
    }

    // Structural-equality compare over JsonNode trees with absolute tolerance
    // on numeric leaves. Used by Exact at GeometryTolerance and by Approx
    // field-by-field at a per-field tol. Lifted here so both modes share one
    // implementation; previously inlined in File.cs as JsonNodesEqualWithinTol.
    public static bool JsonNodesEqualWithinTol(JsonNode? a, JsonNode? b, double tol) {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        switch (a) {
            case JsonObject oa:
                if (b is not JsonObject ob) return false;
                if (oa.Count != ob.Count) return false;
                foreach (var kv in oa) {
                    if (!ob.TryGetPropertyValue(kv.Key, out var bv)) return false;
                    if (!JsonNodesEqualWithinTol(kv.Value, bv, tol)) return false;
                }
                return true;
            case JsonArray aa:
                if (b is not JsonArray ab) return false;
                if (aa.Count != ab.Count) return false;
                for (var i = 0; i < aa.Count; i++) {
                    if (!JsonNodesEqualWithinTol(aa[i], ab[i], tol)) return false;
                }
                return true;
            case JsonValue va:
                if (b is not JsonValue vb) return false;
                if (va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db)) {
                    return Math.Abs(da - db) <= tol;
                }
                if (va.TryGetValue<string>(out var sa) && vb.TryGetValue<string>(out var sb)) {
                    return sa == sb;
                }
                return va.ToJsonString() == vb.ToJsonString();
            default:
                return false;
        }
    }
}
