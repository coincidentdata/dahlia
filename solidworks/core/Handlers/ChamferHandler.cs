using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class ChamferHandler {
    public const string TypeName = "Chamfer";
    public const string SwTypeName = "Chamfer";

    public static JsonNode Add(File file, JsonNode input) {
        var args = ChamferArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);
        // SW quirk: chamfer selection mark is 0 (not 1 like simple fillet).
        Definition.SelectAll(
            file, args.Selections, mark: 0,
            label: args.Kind == ChamferKind.Vertex ? "Chamfer vertex" : "Chamfer edge");

        int mode;
        double width, otherDist, angle;
        double vertexDistA = 0.0, vertexDistB = 0.0, vertexDistC = 0.0;
        if (args.Kind == ChamferKind.Vertex) {
            mode = (int)swChamferType_e.swChamferVertex;
            width = 0.0;
            angle = 0.0;
            otherDist = 0.0;
            // SW quirk: vertexDist1/2/3 are positional against incident-edge enumeration order;
            // apply the inverse of Inspect's stable sort.
            (vertexDistA, vertexDistB, vertexDistC) = OrderVertexDistancesForCreate(
                file, args.Selections[0],
                args.DistanceA!.Value, args.DistanceBVertex!.Value, args.DistanceC!.Value);
        } else if (args.Angle is double a) {
            mode = (int)swChamferType_e.swChamferAngleDistance;
            width = args.Distance!.Value;
            angle = a;
            otherDist = 0.0;
        } else {
            mode = (int)swChamferType_e.swChamferDistanceDistance;
            width = args.Distance!.Value;
            otherDist = args.DistanceB ?? args.Distance!.Value;
            angle = 0.0;
        }

        var options = 0;
        if (args.KeepFeatures) {
            options |= (int)swFeatureChamferOption_e.swFeatureChamferKeepFeature;
        }
        if (args.Kind != ChamferKind.Vertex && args.TangentPropagation) {
            options |= (int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation;
        }
        if (args.Kind != ChamferKind.Vertex
            && args.Angle is not null
            && args.Flipped) {
            options |= (int)swFeatureChamferOption_e.swFeatureChamferFlipDirection;
        }

        var fm = file.ModelDoc.FeatureManager;
        var feature = (Feature?)fm.InsertFeatureChamfer(
            options, mode, width, angle, otherDist,
            vertexDistA, vertexDistB, vertexDistC)
            ?? throw new InvalidOperationException(
                "Chamfer: InsertFeatureChamfer returned null — check "
                + (args.Kind == ChamferKind.Vertex
                    ? "vertex resolved and distances are valid against the local geometry"
                    : "edges resolved and distance/angle are valid against the local geometry"));

        SldworksLog.Information("ChamferHandler.Add: created {Name} on {Count} {What} ({Mode})",
            feature.Name, args.Selections.Count,
            args.Kind == ChamferKind.Vertex ? "vertex" : "edge(s)",
            args.Kind == ChamferKind.Vertex
                ? "Vertex"
                : (args.Angle is null ? "Distance" : "AngleDistance"));
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // Edit an existing chamfer IN PLACE (GetDefinition -> AccessSelections -> mutate ->
    // ModifyDefinition); we do NOT delete + re-add, so the feature name — and every
    // downstream name-ref / probe — survives. `input` is a FULL chamfer payload (same
    // shape Inspect emits / Add consumes), parsed by ChamferArgs so edit and create share
    // one validation. Only the parametric scalars/settings are applied; the edge/vertex
    // SELECTION is kept as built. Mirrors the CircularPattern/Revolve Edit shape.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = ChamferArgs.Parse(input);

        var data = feature.GetDefinition() as IChamferFeatureData2
            ?? throw new InvalidOperationException(
                $"Chamfer.Edit: feature {feature.Name} does not expose IChamferFeatureData2");

        // Refuse Edge<->Vertex kind changes: that's a different selection type, i.e. a
        // different feature (delete + re-add), not an in-place edit.
        var rawType = data.Type;
        var currentIsVertex = rawType == (int)swChamferType_e.swChamferVertex;
        if (currentIsVertex != (args.Kind == ChamferKind.Vertex)) {
            throw new InvalidOperationException(
                $"Chamfer.Edit: cannot change kind on '{feature.Name}' "
                + $"({(currentIsVertex ? "Vertex" : "Edge")} -> {(args.Kind == ChamferKind.Vertex ? "Vertex" : "Edge")});"
                + " that is a different feature — delete and re-add.");
        }

        data.AccessSelections(file.ModelDoc, null);
        try {
            if (args.Kind == ChamferKind.Vertex) {
                // Re-point the chamfered vertex FIRST: the A/B/C distance ordering is derived
                // from the vertex's incident-edge enumeration, so it must reference the NEW
                // vertex's edges, not the old one's.
                var vertex = RepointVertexIfChanged(file, data, args.Selections, feature.Name);
                var order = ParseVertexEdgeOrder(vertex);
                if (order.Count != 3) {
                    throw new InvalidOperationException(
                        $"Chamfer.Edit: vertex chamfer '{feature.Name}' expected 3 incident edges, got {order.Count}");
                }
                // Same A/B/C -> positional remap Create/Inspect use (order[rank] = SW index).
                data.SetVertexChamferDistance(order[0], args.DistanceA!.Value);
                data.SetVertexChamferDistance(order[1], args.DistanceBVertex!.Value);
                data.SetVertexChamferDistance(order[2], args.DistanceC!.Value);
            } else if (args.Angle is double angle) {
                // Re-point edges FIRST (before per-side distance / flip) so the flip and
                // chamfer apply to the new selection, not the old one.
                RepointEdgesIfChanged(file, data, args.Selections, feature.Name);
                data.Type = (int)swChamferType_e.swChamferAngleDistance;
                data.SetEdgeChamferDistance(0, args.Distance!.Value);  // side 0 = primary
                data.EdgeChamferAngle = angle;
                data.TangentPropagation = args.TangentPropagation;
                ApplyFlipToAllEntities(data, args.Flipped);
            } else {
                RepointEdgesIfChanged(file, data, args.Selections, feature.Name);
                data.Type = (int)swChamferType_e.swChamferDistanceDistance;
                data.SetEdgeChamferDistance(0, args.Distance!.Value);
                data.SetEdgeChamferDistance(1, args.DistanceB ?? args.Distance!.Value);  // side 1 = opposite
                data.TangentPropagation = args.TangentPropagation;
            }
            data.KeepFeatures = args.KeepFeatures;

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"Chamfer.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "distance/angle invalid against the local geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree chamfer must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("ChamferHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Re-point a VERTEX chamfer onto a (possibly) different vertex in place. Returns the live
    // vertex the rest of the branch should rank distances against (the new one if re-pointed,
    // else the currently-built one). The incoming vertex def arrives in the Edit payload
    // (ChamferArgs.Selections[0], same ref Add resolves+selects); here we resolve it and set
    // the data interface's IVertex property directly. Edge<->Vertex KIND change is refused
    // upstream; this is Vertex->different-Vertex (same kind) which is in scope.
    private static Vertex RepointVertexIfChanged(
        File file, IChamferFeatureData2 data, IReadOnlyList<Definition> selections, string featureName) {
        // Guard: a vertex chamfer must actually carry an IVertex before we re-point it.
        _ = data.IVertex
            ?? throw new InvalidOperationException(
                $"Chamfer.Edit: vertex chamfer '{featureName}' has no IVertex");
        if (selections.Count != 1) {
            // ChamferArgs already enforces exactly one vertex; defensive.
            throw new InvalidOperationException(
                $"Chamfer.Edit: vertex chamfer '{featureName}' expects exactly one vertex "
                + $"(got {selections.Count}).");
        }

        var live = DefinitionResolver.Resolve(file, selections[0]);
        if (live is null) {
            throw new InvalidOperationException(
                $"Chamfer.Edit: vertex ({selections[0].GetType().Name}) did not resolve to a "
                + $"live entity on '{featureName}'.");
        }
        if (live is not Vertex requested) {
            throw new InvalidOperationException(
                $"Chamfer.Edit: vertex resolved to {live.GetType().Name}, expected Vertex "
                + $"on '{featureName}'.");
        }

        // SW quirk: IVertex is a scalar dispatch property (set_IVertex(Vertex)) — a single
        // live Vertex, NOT a SAFEARRAY, so no DispatchWrapper.
        data.IVertex = requested;
        SldworksLog.Information(
            "ChamferHandler.Edit: re-pointed vertex chamfer '{Name}'", featureName);
        return requested;
    }

    // Re-point an EDGE chamfer onto a (possibly) different edge set in place. The incoming
    // `edges` defs already arrive in the Edit payload (ChamferArgs.Selections, the SAME refs
    // Add resolves+selects). Here — unlike Add (select-then-insert) — we resolve them to live
    // entities and set the feature-data's `Edges` property directly, inside the open
    // AccessSelections block, before ModifyDefinition.
    //
    // Loop / Face seed slots are left as-built: an edge def can't address a Loop or Face seed,
    // so re-pointing would have to DROP them — out of scope for an "edges=" edit.
    private static void RepointEdgesIfChanged(
        File file, IChamferFeatureData2 data, IReadOnlyList<Definition> edgeDefs, string featureName) {
        if (edgeDefs.Count == 0) {
            throw new InvalidOperationException(
                $"Chamfer.Edit: edge chamfer '{featureName}' has an empty edges list.");
        }

        // Resolve the requested refs to live edges (same resolver Add uses).
        var resolvedLive = new List<Edge>(edgeDefs.Count);
        for (var i = 0; i < edgeDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, edgeDefs[i]);
            if (live is null) {
                throw new InvalidOperationException(
                    $"Chamfer.Edit: edges[{i}] ({edgeDefs[i].GetType().Name}) did not resolve "
                    + $"to a live entity on '{featureName}'.");
            }
            if (live is not Edge edge) {
                throw new InvalidOperationException(
                    $"Chamfer.Edit: edges[{i}] resolved to {live.GetType().Name}, expected Edge "
                    + $"on '{featureName}' (Loop/Face re-pointing is out of scope).");
            }
            resolvedLive.Add(edge);
        }

        // Refuse to silently drop Loop/Face seeds: if the built chamfer carries seeds in those
        // slots, an `edges=` re-point can't express them and would change the feature's meaning.
        if (HasLoopOrFaceSeeds(data)) {
            throw new InvalidOperationException(
                $"Chamfer.Edit: '{featureName}' carries Loop/Face chamfer seeds; re-pointing via "
                + "`edges` would drop them — delete and re-add to change those seeds.");
        }

        // SW quirk: feature-data array setters that take SAFEARRAY-of-IDispatch want
        // DispatchWrapper[]; a bare object[] crashes the marshaller (same as HoldLines /
        // AddRelation). `set_Edges(Object)` is the reflection-confirmed setter on
        // IChamferFeatureData2; its getter hands back object[] of Edge, so the wrapped live
        // edges round-trip through it.
        data.Edges = resolvedLive.Select(e => new DispatchWrapper(e)).ToArray();
        SldworksLog.Information(
            "ChamferHandler.Edit: re-pointed '{Name}' onto {Count} edge(s)",
            featureName, resolvedLive.Count);
    }

    private static bool HasLoopOrFaceSeeds(IChamferFeatureData2 data) {
        if (data.Loops is object[] loops && loops.Any(o => o is Loop2)) return true;
        if (data.Faces is object[] faces && faces.Any(o => o is Face2)) return true;
        return false;
    }

    private static void ApplyFlipToAllEntities(IChamferFeatureData2 data, bool flip) {
        var applied = false;
        if (data.Edges is object[] edges) {
            foreach (var entry in edges) {
                if (entry is Edge edge) { data.SetIsFlipped(edge, flip); applied = true; }
            }
        }
        if (data.Loops is object[] loops) {
            foreach (var loopObj in loops) {
                if (loopObj is Loop2 loop && loop.GetEdges() is object[] loopEdges) {
                    foreach (var e in loopEdges) {
                        if (e is Edge edge) { data.SetIsFlipped(edge, flip); applied = true; }
                    }
                }
            }
        }
        if (data.Faces is object[] faces) {
            foreach (var entry in faces) {
                if (entry is Face2 face) { data.SetIsFlipped(face, flip); applied = true; }
            }
        }
        if (!applied) {
            throw new InvalidOperationException("Edit(Chamfer): no chamfered entities to flip.");
        }
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as IChamferFeatureData2
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} does not expose IChamferFeatureData2");

        data.AccessSelections(file.ModelDoc, null);
        try {
            // SW quirk: Type=16 (swChamferEqualDistance) folds back into DistanceDistance.
            var rawType = data.Type;
            var mode = rawType == (int)swChamferType_e.swChamferEqualDistance
                ? swChamferType_e.swChamferDistanceDistance
                : (swChamferType_e)rawType;

            var keepFeatures = data.KeepFeatures;

            if (mode == swChamferType_e.swChamferVertex) {
                return InspectVertex(feature, data, keepFeatures);
            }

            double distance = 0, distanceB = 0, angle = 0;
            switch (mode) {
                case swChamferType_e.swChamferAngleDistance:
                    distance = data.GetEdgeChamferDistance(0);
                    angle = data.EdgeChamferAngle;
                    break;
                case swChamferType_e.swChamferDistanceDistance:
                    distance = data.GetEdgeChamferDistance(0);
                    distanceB = data.GetEdgeChamferDistance(1);
                    break;
            }

            var tangent = data.TangentPropagation;
            var edgesArr = CaptureChamferEdges(data);
            var affected = Definition.CaptureFaces(feature);

            // SW quirk: GetIsFlipped is per-entity but reflects a feature-level toggle.
            var flipped = false;
            if (mode == swChamferType_e.swChamferAngleDistance) {
                flipped = ReadFlippedFromFirstEntity(data);
            }

            var result = new JsonObject {
                ["type"] = TypeName,
                ["kind"] = "Edge",
                ["edges"] = edgesArr,
                ["distance"] = distance,
                ["tangent_propagation"] = tangent,
                ["flipped"] = flipped,
                ["keep_features"] = keepFeatures,
                ["echo_affected_faces"] = affected,
                ["name"] = feature.Name,
            };
            if (mode == swChamferType_e.swChamferDistanceDistance) {
                if (Math.Abs(distanceB - distance) > Flags.GeometryTolerance) {
                    result["distance_b"] = distanceB;
                }
            }
            if (mode == swChamferType_e.swChamferAngleDistance) {
                result["angle"] = angle;
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static JsonNode InspectVertex(Feature feature, IChamferFeatureData2 data, bool keepFeatures) {
        // SW quirk: vertex-chamfer per-edge index is positional and rebuild-fragile;
        // sort by captured-Definition key for stable A/B/C round-trip.
        var vertex = data.IVertex
            ?? throw new InvalidOperationException(
                $"Inspect(Chamfer): vertex chamfer '{feature.Name}' has no IVertex");

        var order = ParseVertexEdgeOrder(vertex);
        if (order.Count != 3) {
            throw new InvalidOperationException(
                $"Inspect(Chamfer): vertex chamfer '{feature.Name}' expected 3 incident "
                + $"edges but got {order.Count}");
        }
        var distA = data.GetVertexChamferDistance(order[0]);
        var distB = data.GetVertexChamferDistance(order[1]);
        var distC = data.GetVertexChamferDistance(order[2]);

        var verticesArr = new JsonArray { DefinitionCapture.Capture(vertex).ToJson() };
        var affected = Definition.CaptureFaces(feature);

        return new JsonObject {
            ["type"] = TypeName,
            ["kind"] = "Vertex",
            ["vertices"] = verticesArr,
            ["distance_a"] = distA,
            ["distance_b_vertex"] = distB,
            ["distance_c"] = distC,
            ["keep_features"] = keepFeatures,
            ["echo_affected_faces"] = affected,
            ["name"] = feature.Name,
        };
    }

    // Deterministic order keyed off geometric Definition so ranking survives rebuild.
    private static List<int> ParseVertexEdgeOrder(Vertex vertex) {
        var order = new List<(int Index, string Key)>();
        if (vertex.GetEdges() is object[] edges) {
            for (var i = 0; i < edges.Length; i++) {
                if (edges[i] is Edge edge) {
                    var key = DefinitionCapture.Capture(edge).ToJson().ToJsonString();
                    order.Add((i, key));
                }
            }
        }
        order.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return order.Select(p => p.Index).ToList();
    }

    // Inverse of Inspect's sort: map user A/B/C onto SW's positional vertex-edge indices.
    private static (double, double, double) OrderVertexDistancesForCreate(
        File file, Definition vertexDef, double distA, double distB, double distC) {
        var live = DefinitionResolver.Resolve(file, vertexDef)
            ?? throw new InvalidOperationException(
                "Chamfer[Vertex]: vertex did not resolve");
        if (live is not Vertex vertex) {
            throw new InvalidOperationException(
                $"Chamfer[Vertex]: resolved entity is {live.GetType().Name}, expected Vertex");
        }

        // sortedOrder[k] = positional index of the edge ranked k.
        var sortedOrder = ParseVertexEdgeOrder(vertex);
        if (sortedOrder.Count != 3) {
            throw new InvalidOperationException(
                $"Chamfer[Vertex]: expected 3 incident edges at the vertex, got {sortedOrder.Count}");
        }
        var perPos = new double[3];
        var distances = new[] { distA, distB, distC };
        for (var rank = 0; rank < 3; rank++) {
            perPos[sortedOrder[rank]] = distances[rank];
        }
        return (perPos[0], perPos[1], perPos[2]);
    }

    private static JsonArray CaptureChamferEdges(IChamferFeatureData2 data) {
        var arr = new JsonArray();

        // SW quirk: chamfer data exposes Edges, Loops, and Faces as separate selection sets.
        if (data.Edges is object[] edges) {
            foreach (var entry in edges) {
                if (entry is Edge edge) {
                    arr.Add(DefinitionCapture.Capture(edge).ToJson());
                }
            }
        }
        if (data.Loops is object[] loops) {
            foreach (var loopObj in loops) {
                if (loopObj is not Loop2 loop) continue;
                if (loop.GetEdges() is object[] loopEdges) {
                    foreach (var e in loopEdges) {
                        if (e is Edge edge) {
                            arr.Add(DefinitionCapture.Capture(edge).ToJson());
                        }
                    }
                }
            }
        }
        if (data.Faces is object[] faces) {
            foreach (var entry in faces) {
                if (entry is Face2 face) {
                    arr.Add(DefinitionCapture.Capture(face).ToJson());
                }
            }
        }
        return arr;
    }

    private static bool ReadFlippedFromFirstEntity(IChamferFeatureData2 data) {
        if (data.Edges is object[] edges) {
            foreach (var entry in edges) {
                if (entry is Edge edge) {
                    return data.GetIsFlipped(edge);
                }
            }
        }
        if (data.Loops is object[] loops) {
            foreach (var loopObj in loops) {
                if (loopObj is Loop2 loop && loop.GetEdges() is object[] loopEdges) {
                    foreach (var e in loopEdges) {
                        if (e is Edge edge) {
                            return data.GetIsFlipped(edge);
                        }
                    }
                }
            }
        }
        if (data.Faces is object[] faces) {
            foreach (var entry in faces) {
                if (entry is Face2 face) {
                    return data.GetIsFlipped(face);
                }
            }
        }
        return false;
    }

    private enum ChamferKind {
        Edge,
        Vertex,
    }

    private sealed record ChamferArgs(
        ChamferKind Kind,
        IReadOnlyList<Definition> Selections,
        double? Distance,
        double? DistanceB,
        double? Angle,
        bool TangentPropagation,
        bool Flipped,
        bool KeepFeatures,
        double? DistanceA,
        double? DistanceBVertex,
        double? DistanceC) {

        internal static ChamferArgs Parse(JsonNode input) {
            var kindStr = (input["kind"]?.GetValue<string>()) ?? "Edge";
            var kind = kindStr switch {
                "Edge" => ChamferKind.Edge,
                "Vertex" => ChamferKind.Vertex,
                _ => throw new ArgumentException(
                    $"Chamfer: unknown kind '{kindStr}' (expected 'Edge' or 'Vertex')"),
            };
            var keepFeatures = JsonHelpers.ReadBool(input, "keep_features", true);

            if (kind == ChamferKind.Vertex) {
                var distA = JsonHelpers.ReadDouble(input, "distance_a");
                var distB = JsonHelpers.ReadDouble(input, "distance_b_vertex");
                var distC = JsonHelpers.ReadDouble(input, "distance_c");
                if (distA <= 0.0 || distB <= 0.0 || distC <= 0.0) {
                    throw new ArgumentException(
                        $"Chamfer[Vertex]: distance_a/_b_vertex/_c must be > 0 "
                        + $"(got {distA}, {distB}, {distC})");
                }
                var vertices = Definition.FromJsonArray(
                    input["vertices"], "Chamfer.vertices");
                if (vertices.Count == 0) {
                    throw new ArgumentException("Chamfer[Vertex]: vertices list is empty");
                }
                if (vertices.Count > 1) {
                    // SW quirk: vertex chamfers are single-vertex per feature.
                    throw new ArgumentException(
                        $"Chamfer[Vertex]: exactly one vertex per chamfer feature "
                        + $"(got {vertices.Count})");
                }
                return new ChamferArgs(
                    Kind: ChamferKind.Vertex,
                    Selections: vertices,
                    Distance: null, DistanceB: null, Angle: null,
                    TangentPropagation: false,
                    Flipped: false,
                    KeepFeatures: keepFeatures,
                    DistanceA: distA, DistanceBVertex: distB, DistanceC: distC);
            }

            // Edge variant
            var distance = JsonHelpers.ReadDouble(input, "distance");
            if (distance <= 0.0) {
                throw new ArgumentException($"Chamfer: distance must be > 0 (got {distance})");
            }
            var distanceB = JsonHelpers.ReadOptionalDouble(input, "distance_b");
            var angle = JsonHelpers.ReadOptionalDouble(input, "angle");
            var tangent = JsonHelpers.ReadBool(input, "tangent_propagation", true);
            var flipped = JsonHelpers.ReadBool(input, "flipped", false);
            var edges = Definition.FromJsonArray(input["edges"], "Chamfer.edges");
            if (edges.Count == 0) {
                throw new ArgumentException("Chamfer: edges list is empty");
            }

            if (angle is not null && distanceB is not null) {
                throw new ArgumentException(
                    "Chamfer: 'angle' and 'distance_b' are mutually exclusive — pick one mode");
            }
            return new ChamferArgs(
                Kind: ChamferKind.Edge,
                Selections: edges,
                Distance: distance, DistanceB: distanceB, Angle: angle,
                TangentPropagation: tangent,
                Flipped: flipped,
                KeepFeatures: keepFeatures,
                DistanceA: null, DistanceBVertex: null, DistanceC: null);
        }
    }
}
