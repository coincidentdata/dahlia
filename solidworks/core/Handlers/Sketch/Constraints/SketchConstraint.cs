using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Sketches;

// Constraint dispatch and entry points. Owns the JSON-level Add/Read entry points called
// by the SketchHandler orchestrator, the per-relation Read dispatch used during Inspect,
// the per-Add ref resolver (straight DefinitionResolver lookup, since refs carry
// target-side SketchEntityIds by the time AddConstraints runs), and the kind→class
// fan-out that routes to RelationConstraint, DimensionConstraint, or AngleDimensionConstraint.
internal static class SketchConstraint {
    // High-level write entry: parses the JSON kind, dispatches to the per-kind class.
    // Each per-kind class is responsible for resolving its own refs — RelationConstraint
    // takes the raw refs JsonArray (resolves internally for AddRelation, self-selects
    // each ref via Definition.Select for the legacy path), DimensionConstraint and
    // AngleDimensionConstraint pre-resolve here since their wire format binds refs to
    // ordered positions.
    internal static void Add(File file, Sketch sketch, JsonNode node) {
        var kindStr = node["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("Constraint payload missing 'kind'");
        if (!Enum.TryParse<ConstraintKind>(kindStr, ignoreCase: false, out var kind)) {
            throw new ArgumentException($"Constraint kind '{kindStr}' not recognized");
        }
        var refsArr = node["refs"]?.AsArray()
            ?? throw new ArgumentException($"Constraint {kind}: missing refs");

        if (RelationConstraint.HandlesKind(kind)) {
            RelationConstraint.Add(file, sketch, kind, refsArr);
            return;
        }

        var resolved = new List<object>(refsArr.Count);
        foreach (var refNode in refsArr) {
            object live = ResolveRef(file, sketch, refNode)
                ?? throw new InvalidOperationException(
                    $"Constraint {kind}: ref {refNode?.ToJsonString()} did not resolve to a live sketch entity");
            resolved.Add(live);
        }

        if (kind == ConstraintKind.Angle) {
            AngleDimensionConstraint.Add(file, sketch, node, resolved);
            return;
        }
        if (DimensionConstraint.HandlesKind(kind)) {
            DimensionConstraint.Add(file, sketch, kind, node, resolved);
            return;
        }
        throw new ArgumentException($"Constraint kind '{kind}' not supported");
    }

    // High-level read entry: walks every relation on the sketch and emits one JSON entry per
    // recognized constraint. Suppressed/empty relations skipped silently; structural kinds
    // (Patterned/OffsetEdge/SketchOffset) skipped via the per-relation dispatch.
    //
    // `emittedIds` is the (kind, id) set of entities the caller actually emitted into
    // entities[] — SketchHandler.ReadEntities populates it as a side effect of the
    // entity walk, so any (kind, id) here is guaranteed to be on the wire. The
    // constraint walker gates each ref against this set; refs to internal SW-only
    // markers we skipped (silhouettes, etc.) cause the whole constraint to be dropped.
    internal static JsonArray ReadAll(File file, Sketch sketch, HashSet<(string Kind, long Id)> emittedIds) {
        var arr = new JsonArray();
        var manager = sketch.RelationManager;
        if (manager?.GetRelations((int)swSketchRelationFilterType_e.swAll) is not object[] relations) {
            return arr;
        }
        foreach (SketchRelation relation in relations) {
            if (relation.Suppressed) continue;
            if (relation.GetEntitiesCount() <= 0) continue;
            var entry = Read(file, sketch, relation, emittedIds);
            if (entry is not null) arr.Add(entry);
        }
        return arr;
    }

    // SW swSketchRelations_e values for the structural relation types that back high-level
    // composite entities (sketch pattern, internal/external offset). OffsetEdge and
    // SketchOffset are still dropped at the per-relation read pass because their
    // composite parsers (Phase 4) aren't yet wired. Patterned IS surfaced because the
    // PolygonParser consumes it on Inspect to re-form the SketchPolygon composite — any
    // Patterned constraint that PolygonParser doesn't fold into a polygon gets stripped
    // before the constraints array is returned.
    private const int RelationOffsetEdge   = 16;
    private const int RelationSketchOffset = 47;

    // Per-relation read dispatch.
    private static JsonObject? Read(File file, Sketch sketch, SketchRelation relation,
                                    HashSet<(string Kind, long Id)> emittedIds) {
        var typeInt = relation.GetRelationType();
        var relationKind = RelationConstraint.KindNameForTypeInt(typeInt);
        SldworksLog.Information(
            "Inspect(Sketch): reading constraint relation type {TypeInt} ({Kind})",
            typeInt, relationKind);
        if (typeInt == RelationOffsetEdge ||
            typeInt == RelationSketchOffset) {
            SldworksLog.Information(
                "Inspect(Sketch): skipping structural constraint relation type {TypeInt} ({Kind})",
                typeInt, relationKind);
            return null;
        }
        var displayDim = relation.GetDisplayDimension() as DisplayDimension;
        if (displayDim is null) {
            return RelationConstraint.Read(file, sketch, relation, typeInt, emittedIds);
        }
        // Use DimensionConstraint.ResolveDimType for routing — Type2 lies for the doubled
        // dim variants (DoubleDistance reports as Diameter, DoubleAngle reports as Angular)
        // and dispatching on raw Type2 would mis-route DoubleAngle to AngleDimensionConstraint.
        var dimType = DimensionConstraint.ResolveDimType(relation, displayDim);
        SldworksLog.Information(
            "Inspect(Sketch): reading dimension constraint relation type {TypeInt} ({Kind}); " +
            "display Type2={DisplayType} routed dimType={DimType}",
            typeInt, relationKind, displayDim.Type2, dimType);
        if (dimType == DimensionConstraint.Angular) {
            return AngleDimensionConstraint.Read(file, sketch, relation, displayDim, emittedIds);
        }
        return DimensionConstraint.Read(file, sketch, relation, displayDim, emittedIds);
    }

    // Resolves one constraint ref. The wire shape is always a Definition JSON object —
    // typically a SketchEntityDefinition (sketch_line / sketch_arc / sketch_point / ...)
    // or a bare SketchEntityId. The caller has already back-filled target-side ids onto
    // the Python-side entities before serializing this payload, so the SketchEntityId
    // triplet in each ref points at a real entity in the active sketch (or a fixture
    // entity in another sketch), and DefinitionResolver can find it directly. The
    // active-sketch hint covers the case where a same-sketch ref omits sketch_name.
    private static object? ResolveRef(File file, Sketch sketch, JsonNode? refNode) {
        if (refNode is null) return null;
        if (refNode is not JsonObject) {
            throw new ArgumentException(
                $"Constraint ref must be a Definition JSON object; got {refNode.ToJsonString()}");
        }

        var def = Definition.FromJson(refNode)
            ?? throw new ArgumentException(
                $"Constraint ref {refNode.ToJsonString()}: not a valid Definition");

        return DefinitionResolver.Resolve(file, def, sketch)
            ?? throw new InvalidOperationException(
                $"Constraint ref {refNode.ToJsonString()}: Definition did not resolve to a live entity");
    }
}
