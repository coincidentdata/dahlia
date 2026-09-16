using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Composites;

// Sketch linear pattern composite. Patterns each seed entity into a 1D or 2D grid
// via SW's SketchManager.CreateLinearSketchStepAndRepeat. The seed itself sits at
// grid position (0, 0) and is NOT counted as an instance — each seed produces
// (count_a * count_b - 1) instances.
//
// Wire shape:
//   type:        "LinearPattern" — composite, type-discriminated.
//   seeds:       SketchEntityIds of entities to pattern. Resolved via
//                DefinitionResolver against the active sketch.
//   entities:    2D list of typed flavor Definitions; outer index = per-seed
//                (matches `seeds` order), inner index walks (count_a*count_b - 1)
//                instance positions in SW's append order. Empty on input;
//                Inspect populates with target instances.
//   deleted:     Parallel 2D bool list. true = the user deleted that specific
//                instance from the pattern; the entity at that slot was
//                captured before delete and remains addressable on the wire as
//                a stale def (deleted=true is the signal it isn't live).
//   angle_a:     Primary direction angle in radians. Matches SW's
//                CreateLinearSketchStepAndRepeat angleX arg.
//   spacing_a:   Primary spacing in meters (the wire is meter-native).
//                Matches CreateLinearSketchStepAndRepeat distanceX arg.
//   count_a:     Primary count (>= 1).
//   angle_b/spacing_b/count_b:
//                Secondary direction. For a 1D pattern set count_b = 1
//                (angle_b / spacing_b are then ignored by SW).
//   construction:Inherited from _CompositeSketchEntity — applied to each
//                produced instance segment.
public sealed record SketchLinearPattern(
    [property: JsonPropertyName("type")]   string Type,
    [property: JsonPropertyName("seeds")]  IReadOnlyList<SketchEntityId> Seeds,
    [property: JsonPropertyName("angle_a")]   double AngleA,
    [property: JsonPropertyName("spacing_a")] double SpacingA,
    [property: JsonPropertyName("count_a")]   int CountA,
    [property: JsonPropertyName("angle_b")]   double AngleB = 0.0,
    [property: JsonPropertyName("spacing_b")] double SpacingB = 0.0,
    [property: JsonPropertyName("count_b")]   int CountB = 1,
    [property: JsonPropertyName("construction")] bool Construction = false,
    [property: JsonPropertyName("entities")] IReadOnlyList<IReadOnlyList<SketchEntityDefinition>>? Entities = null,
    [property: JsonPropertyName("deleted")]  IReadOnlyList<IReadOnlyList<bool>>? Deleted = null) : ISketchEntity {

    public const string TypeName = "LinearPattern";

    // Captured target-side instance defs at Add time, indexed [seed_idx][instance_idx].
    // Outer length = Seeds.Count, inner length = CountA*CountB - 1. For deleted slots
    // the def is captured before SW removes the live entity, so it stays addressable
    // on the wire (with the now-stale id) but the parallel `_deletedFlags[i][j]`
    // marks it as not-live. Both populated together inside Add.
    private List<List<SketchEntityDefinition>>? _capturedInstances;
    private List<List<bool>>? _capturedDeleted;

    internal SketchLinearPattern Add(File file, Sketch sketch, SketchManager sketchManager) {
        if (CountA < 1 || CountB < 1) {
            throw new ArgumentException(
                $"SketchLinearPattern.Add: counts must be >= 1 (got count_a={CountA}, count_b={CountB})");
        }
        if (Seeds.Count == 0) {
            throw new ArgumentException("SketchLinearPattern.Add: seeds must contain at least one SketchEntityId");
        }

        int instancesPerSeed = CountA * CountB - 1;
        var sketchName = ((Feature)sketch).Name ?? "";

        // Snapshot: counts of segments per concrete SW type, plus point count.
        // After the pattern step we'll diff to identify produced entities.
        var preSegByGroup = SnapshotSegmentsByGroup(sketch);
        int prePointCount = (sketch.GetSketchPoints2() as object[])?.Length ?? 0;

        // Resolve and select all seeds onto mark 0 — SW's
        // CreateLinearSketchStepAndRepeat reads the selection set as the seed list.
        file.ModelDoc.ClearSelection2(true);
        var resolvedSeeds = new List<object>(Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++) {
            var live = DefinitionResolver.Resolve(file, Seeds[i], sketch)
                ?? throw new InvalidOperationException(
                    $"SketchLinearPattern.Add: seed[{i}] (kind={Seeds[i].EntityKind}, id={Seeds[i].Id}) did not resolve to a live entity");
            if (!Definition.SelectLive(file, live, 0)) {
                throw new InvalidOperationException(
                    $"SketchLinearPattern.Add: SW rejected selection of seed[{i}] ({live.GetType().Name})");
            }
            resolvedSeeds.Add(live);
        }

        // SW quirk: CreateLinearSketchStepAndRepeat takes
        // (countX, countY, distanceX, distanceY, angleX, angleY, dimXLabel,
        //  flipDimX, flipDimY, dimToInstance, dimToInstanceX, dimToInstanceY).
        // The trailing six bools are display-only and don't affect geometry.
        if (!sketchManager.CreateLinearSketchStepAndRepeat(
                CountA, CountB, SpacingA, SpacingB, AngleA, AngleB,
                "", false, false, false, false, false)) {
            throw new InvalidOperationException(
                $"SketchLinearPattern.Add: CreateLinearSketchStepAndRepeat(count_a={CountA}, " +
                $"count_b={CountB}, spacing_a={SpacingA}, spacing_b={SpacingB}, " +
                $"angle_a={AngleA}, angle_b={AngleB}) returned false");
        }

        // Map the produced entities back to (seed_idx, instance_idx). SW emits
        // new entities of each concrete type in append order, grouped first by
        // seed-of-that-type and then by instance-grid-position.
        var liveInstances = MapInstancesToSeeds(
            sketch, resolvedSeeds, preSegByGroup, prePointCount, instancesPerSeed);

        // Capture each live instance to a typed Definition flavor BEFORE applying
        // user-deletions (so deleted slots still carry geometry on the wire).
        var capturedDefs = new List<List<SketchEntityDefinition>>(Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++) {
            var perSeedDefs = new List<SketchEntityDefinition>(instancesPerSeed);
            for (int j = 0; j < instancesPerSeed; j++) {
                var live = liveInstances[i][j];
                perSeedDefs.Add(CaptureInstance(live, sketchName));
            }
            capturedDefs.Add(perSeedDefs);
        }

        // Apply construction flag per instance segment (points have no construction).
        if (Construction) {
            for (int i = 0; i < Seeds.Count; i++) {
                for (int j = 0; j < instancesPerSeed; j++) {
                    if (liveInstances[i][j] is SketchSegment seg) {
                        seg.ConstructionGeometry = true;
                    }
                }
            }
        }

        // Apply user-deleted markers: select live entities for the marked
        // (seed_idx, instance_idx) slots and DeleteSelection2 them. The captured
        // defs stay on the wire so the user can see what *was* there.
        var deletedFlags = new List<List<bool>>(Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++) {
            deletedFlags.Add(new List<bool>(new bool[instancesPerSeed]));
        }
        if (Deleted is not null && Deleted.Count > 0) {
            file.ModelDoc.ClearSelection2(true);
            int totalToDelete = 0;
            for (int i = 0; i < Math.Min(Deleted.Count, Seeds.Count); i++) {
                var perSeed = Deleted[i];
                for (int j = 0; j < Math.Min(perSeed.Count, instancesPerSeed); j++) {
                    if (!perSeed[j]) continue;
                    deletedFlags[i][j] = true;
                    Definition.SelectLive(file, liveInstances[i][j], 0);
                    totalToDelete++;
                }
            }
            if (totalToDelete > 0) {
                // SW quirk: pass 0 (not swDelete_Children) so DeleteSelection2 fails
                // hard if SW refuses (e.g. dependent constraints), surfacing the
                // problem instead of cascading silently.
                if (!file.ModelDoc.Extension.DeleteSelection2(0)) {
                    throw new InvalidOperationException(
                        $"SketchLinearPattern.Add: DeleteSelection2 refused to delete {totalToDelete} marked instances " +
                        "(likely a dependent constraint). Remove the dependents or unmark the slot.");
                }
            }
        }

        _capturedInstances = capturedDefs;
        _capturedDeleted = deletedFlags;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_capturedInstances is null || _capturedDeleted is null) {
            throw new InvalidOperationException(
                "SketchLinearPattern.GetJson called before Add — no captured instances");
        }
        var entitiesArr = new JsonArray();
        var deletedArr = new JsonArray();
        for (int i = 0; i < _capturedInstances.Count; i++) {
            var perSeedDefs = new JsonArray();
            var perSeedDel = new JsonArray();
            for (int j = 0; j < _capturedInstances[i].Count; j++) {
                perSeedDefs.Add(_capturedInstances[i][j].ToJson());
                perSeedDel.Add(_capturedDeleted[i][j]);
            }
            entitiesArr.Add(perSeedDefs);
            deletedArr.Add(perSeedDel);
        }
        var seedsArr = new JsonArray();
        foreach (var seed in Seeds) {
            seedsArr.Add(seed.ToJson());
        }
        return new JsonObject {
            ["type"]         = TypeName,
            ["seeds"]        = seedsArr,
            ["angle_a"]      = AngleA,
            ["spacing_a"]    = SpacingA,
            ["count_a"]      = CountA,
            ["angle_b"]      = AngleB,
            ["spacing_b"]    = SpacingB,
            ["count_b"]      = CountB,
            ["construction"] = Construction,
            ["entities"]     = entitiesArr,
            ["deleted"]      = deletedArr,
        };
    }

    // ---- helpers ---------------------------------------------------------------

    // Capture a live SW pattern instance (SketchSegment or SketchPoint) to the
    // typed flavor Definition. Routes to DefinitionCapture which handles the
    // segment-vs-point split internally.
    private static SketchEntityDefinition CaptureInstance(object live, string sketchName) =>
        live switch {
            SketchSegment seg => DefinitionCapture.Capture(seg, sketchName),
            SketchPoint pt    => DefinitionCapture.Capture(pt, sketchName),
            _ => throw new InvalidOperationException(
                $"SketchLinearPattern.CaptureInstance: unexpected entity type {live.GetType().Name} " +
                "(expected SketchSegment or SketchPoint)"),
        };

    // Group ids classify SW sketch entities into the buckets SW's pattern-step
    // emits instances into. Interface checks (`is SketchLine` etc.) are reliable;
    // `GetType()` is NOT — pre-existing entities come back as SketchSegmentClass
    // RCWs from a prior GetSketchSegments call while newly-created entities come
    // back as the specific RCW (SketchLineClass) for the same SW entity. Comparing
    // by interface sidesteps the RCW mismatch.
    private const int GroupArcOrCircle = 1;
    private const int GroupEllipse     = 2;
    private const int GroupLine        = 3;
    private const int GroupParabola    = 4;
    private const int GroupPoint       = 5;
    private const int GroupSpline      = 6;

    // SW quirk: SketchEllipse covers BOTH closed ellipses and elliptical arcs
    // (same COM class, distinguished by SketchEllipse.IsClosed). They go in the
    // same group either way — SW's pattern step emits new instances as the same
    // type as the seed.
    internal static int GetGroupId(object entity) =>
        entity switch {
            SketchLine     => GroupLine,
            SketchArc      => GroupArcOrCircle,
            SketchEllipse  => GroupEllipse,
            SketchParabola => GroupParabola,
            SketchSpline   => GroupSpline,
            SketchPoint    => GroupPoint,
            _ => throw new InvalidOperationException(
                $"SketchLinearPattern.GetGroupId: unsupported sketch entity type {entity.GetType().Name}"),
        };

    // Counts segments by group id so the diff after the pattern step can
    // identify "new segments of group G". Skips points (tracked via
    // prePointCount in the pattern Add path).
    internal static Dictionary<int, int> SnapshotSegmentsByGroup(Sketch sketch) {
        var map = new Dictionary<int, int>();
        var segments = sketch.GetSketchSegments() as object[];
        if (segments is not null) {
            foreach (var seg in segments) {
                var g = GetGroupId(seg);
                map[g] = map.GetValueOrDefault(g, 0) + 1;
            }
        }
        return map;
    }

    // After the pattern step, the sketch has the original entities + new
    // instance entities of the same group as the seeds. SW emits new entries
    // in append order, grouped per seed-of-that-group and then per grid
    // position. Returns a List<List<object>> of shape (seedCount, instancesPerSeed)
    // of live SW entities (SketchSegment or SketchPoint).
    internal static List<List<object>> MapInstancesToSeeds(
            Sketch sketch, List<object> seeds,
            Dictionary<int, int> preSegByGroup, int prePointCount,
            int instancesPerSeed) {
        // Group seeds by group id, preserving input-order indices.
        var seedGroupBuckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < seeds.Count; i++) {
            var g = GetGroupId(seeds[i]);
            if (!seedGroupBuckets.TryGetValue(g, out var list)) {
                list = new List<int>();
                seedGroupBuckets[g] = list;
            }
            list.Add(i);
        }

        var result = new List<List<object>>(seeds.Count);
        for (int i = 0; i < seeds.Count; i++) {
            result.Add(new List<object>(instancesPerSeed));
            for (int j = 0; j < instancesPerSeed; j++) {
                result[i].Add(null!); // filled below
            }
        }

        var segmentsArr = (sketch.GetSketchSegments() as object[]) ?? Array.Empty<object>();
        var pointsArr = (sketch.GetSketchPoints2() as object[]) ?? Array.Empty<object>();

        foreach (var (groupId, seedIndices) in seedGroupBuckets) {
            int seedCountForGroup = seedIndices.Count;
            int expected = seedCountForGroup * instancesPerSeed;
            if (groupId == GroupPoint) {
                int newCount = pointsArr.Length - prePointCount;
                if (newCount < expected) {
                    throw new InvalidOperationException(
                        $"SketchLinearPattern.MapInstancesToSeeds: expected {expected} new SketchPoints; got {newCount}");
                }
                AssignChunks(seedIndices, instancesPerSeed, pointsArr, prePointCount, result);
            } else {
                // Slice segments by group, then slice the new entries.
                var matching = new List<object>();
                foreach (var seg in segmentsArr) {
                    if (GetGroupId(seg) == groupId) matching.Add(seg);
                }
                int preCount = preSegByGroup.GetValueOrDefault(groupId, 0);
                int newCount = matching.Count - preCount;
                if (newCount < expected) {
                    throw new InvalidOperationException(
                        $"SketchLinearPattern.MapInstancesToSeeds: expected {expected} new group-{groupId} segments; got {newCount}");
                }
                AssignChunks(seedIndices, instancesPerSeed, matching, preCount, result);
            }
        }
        return result;
    }

    // SW append order for pattern instances: for each grid position k in
    // [0..instancesPerSeed), emit one entry per seed-of-type. So the array layout
    // starting at `baseIdx` is:
    //   [seed0_pos0, seed1_pos0, ..., seedN_pos0, seed0_pos1, seed1_pos1, ...]
    // i.e. seedIdx within group, then instance position.
    private static void AssignChunks(
            List<int> seedIndices, int instancesPerSeed,
            IList<object> source, int sourceBase,
            List<List<object>> result) {
        int seedCountForType = seedIndices.Count;
        for (int seedPosInGroup = 0; seedPosInGroup < seedCountForType; seedPosInGroup++) {
            var globalSeedIdx = seedIndices[seedPosInGroup];
            for (int instJ = 0; instJ < instancesPerSeed; instJ++) {
                int srcIdx = sourceBase + seedPosInGroup + seedCountForType * instJ;
                result[globalSeedIdx][instJ] = source[srcIdx];
            }
        }
    }
}
