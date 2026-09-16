using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Composites;

// Sketch circular pattern composite. Patterns each seed entity around a center
// point via SW's SketchManager.CreateCircularSketchStepAndRepeat. The seed sits
// at angle 0 and is NOT counted as an instance — each seed produces (count - 1)
// instances.
//
// Wire shape:
//   type:        "CircularPattern" — composite, type-discriminated.
//   seeds:       SketchEntityIds of entities to pattern.
//   entities:    2D list of typed flavor Definitions; outer = per-seed, inner =
//                (count - 1) angular instances in append order. Empty on input;
//                Inspect populates with target instances.
//   deleted:     Parallel 2D bool list; true = user deleted that instance.
//   center:      Point2D in sketch-local coords. The pattern is centered here.
//   angle_step:  Radians between successive instances. Positive = CCW. The Add
//                path negates internally because SW's API uses positive = CW.
//   count:       Total instance count INCLUDING the seed at angle 0.
//   construction: Inherited; applied to each produced instance segment.
public sealed record SketchCircularPattern(
    [property: JsonPropertyName("type")]   string Type,
    [property: JsonPropertyName("seeds")]  IReadOnlyList<SketchEntityId> Seeds,
    [property: JsonPropertyName("center")] Point2D Center,
    [property: JsonPropertyName("angle_step")] double AngleStep,
    [property: JsonPropertyName("count")]  int Count,
    [property: JsonPropertyName("construction")] bool Construction = false,
    [property: JsonPropertyName("entities")] IReadOnlyList<IReadOnlyList<SketchEntityDefinition>>? Entities = null,
    [property: JsonPropertyName("deleted")]  IReadOnlyList<IReadOnlyList<bool>>? Deleted = null) : ISketchEntity {

    public const string TypeName = "CircularPattern";

    private List<List<SketchEntityDefinition>>? _capturedInstances;
    private List<List<bool>>? _capturedDeleted;

    internal SketchCircularPattern Add(File file, Sketch sketch, SketchManager sketchManager) {
        if (Count < 1) {
            throw new ArgumentException(
                $"SketchCircularPattern.Add: count must be >= 1 (got {Count})");
        }
        if (Seeds.Count == 0) {
            throw new ArgumentException("SketchCircularPattern.Add: seeds must contain at least one SketchEntityId");
        }

        int instancesPerSeed = Count - 1;
        var sketchName = ((Feature)sketch).Name ?? "";

        // Resolve seeds first (also used to compute bbox center for the angle/radius args).
        var resolvedSeeds = new List<object>(Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++) {
            var live = DefinitionResolver.Resolve(file, Seeds[i], sketch)
                ?? throw new InvalidOperationException(
                    $"SketchCircularPattern.Add: seed[{i}] (kind={Seeds[i].EntityKind}, id={Seeds[i].Id}) did not resolve to a live entity");
            resolvedSeeds.Add(live);
        }

        // SW quirk: CreateCircularSketchStepAndRepeat takes
        // (radius, startAngle, count, angleStep, equalSpacing, dimLabel, ...). The
        // (radius, startAngle) pair locates the rotation center relative to the
        // seeds' geometric bbox center (NOT the seed-side of that vector). The
        // direction passed is `Center - GeoCenter` — the vector pointing FROM the
        // seeds TO the rotation center. Flipping the sign moves instances to the
        // wrong side and they overlap pre-existing geometry.
        var (gcx, gcy) = ComputeSeedsGeometricCenter(resolvedSeeds, sketchName);
        var dx = Center.X - gcx;
        var dy = Center.Y - gcy;
        var radius = Math.Sqrt(dx * dx + dy * dy);
        var startAngle = NormalizeAngle(Math.Atan2(dy, dx));

        // Snapshot for diff-mapping.
        var preSegByGroup = SketchLinearPattern.SnapshotSegmentsByGroup(sketch);
        int prePointCount = (sketch.GetSketchPoints2() as object[])?.Length ?? 0;

        // Select all seeds onto mark 0.
        file.ModelDoc.ClearSelection2(true);
        for (int i = 0; i < resolvedSeeds.Count; i++) {
            if (!Definition.SelectLive(file, resolvedSeeds[i], 0)) {
                throw new InvalidOperationException(
                    $"SketchCircularPattern.Add: SW rejected selection of seed[{i}] ({resolvedSeeds[i].GetType().Name})");
            }
        }

        // SW quirk: positive angleStep = clockwise in SW's API. The wire convention
        // is positive = CCW (matches math.atan2), so negate at the boundary.
        if (!sketchManager.CreateCircularSketchStepAndRepeat(
                radius, startAngle, Count, -AngleStep, true, "", false, false, false)) {
            throw new InvalidOperationException(
                $"SketchCircularPattern.Add: CreateCircularSketchStepAndRepeat(radius={radius}, " +
                $"startAngle={startAngle}, count={Count}, angleStep={-AngleStep}) returned false");
        }

        // SW quirk: CreateCircularSketchStepAndRepeat ALSO emits a center SketchPoint
        // (the rotation axis indicator). That extra point lives in the sketch points
        // array at the tail; it must be excluded from the instance-mapping that
        // assumes only seed-typed entities are added. The mapping helper accounts
        // for it by reserving the last point slot for the center indicator.
        var liveInstances = MapInstancesToSeedsWithCenterPoint(
            sketch, resolvedSeeds, preSegByGroup, prePointCount, instancesPerSeed);

        // Capture each live instance to a typed Definition flavor BEFORE deletes.
        var capturedDefs = new List<List<SketchEntityDefinition>>(Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++) {
            var perSeedDefs = new List<SketchEntityDefinition>(instancesPerSeed);
            for (int j = 0; j < instancesPerSeed; j++) {
                perSeedDefs.Add(CaptureInstance(liveInstances[i][j], sketchName));
            }
            capturedDefs.Add(perSeedDefs);
        }

        // Construction flag propagation (segments only).
        if (Construction) {
            for (int i = 0; i < Seeds.Count; i++) {
                for (int j = 0; j < instancesPerSeed; j++) {
                    if (liveInstances[i][j] is SketchSegment seg) {
                        seg.ConstructionGeometry = true;
                    }
                }
            }
        }

        // Apply user-deleted markers.
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
                if (!file.ModelDoc.Extension.DeleteSelection2(0)) {
                    throw new InvalidOperationException(
                        $"SketchCircularPattern.Add: DeleteSelection2 refused to delete {totalToDelete} marked instances " +
                        "(likely a dependent constraint).");
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
                "SketchCircularPattern.GetJson called before Add — no captured instances");
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
            ["center"]       = new JsonObject { ["x"] = Center.X, ["y"] = Center.Y },
            ["angle_step"]   = AngleStep,
            ["count"]        = Count,
            ["construction"] = Construction,
            ["entities"]     = entitiesArr,
            ["deleted"]      = deletedArr,
        };
    }

    // ---- helpers ---------------------------------------------------------------

    private static SketchEntityDefinition CaptureInstance(object live, string sketchName) =>
        live switch {
            SketchSegment seg => DefinitionCapture.Capture(seg, sketchName),
            SketchPoint pt    => DefinitionCapture.Capture(pt, sketchName),
            _ => throw new InvalidOperationException(
                $"SketchCircularPattern.CaptureInstance: unexpected entity type {live.GetType().Name}"),
        };

    // Compute the geometric (axis-aligned bbox) center of all seed entities by
    // walking each seed's addressable points. SW's CreateCircularSketchStepAndRepeat
    // wants the seed's CURRENT polar position (radius + startAngle) relative to the
    // requested center; the bbox center provides that for multi-seed patterns.
    private static (double cx, double cy) ComputeSeedsGeometricCenter(List<object> seeds, string sketchName) {
        double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity;
        double xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
        foreach (var seed in seeds) {
            var seedDefinition = CaptureInstance(seed, sketchName);
            foreach (var p in seedDefinition.PatternExtentPoints()) {
                if (p.X < xmin) xmin = p.X;
                if (p.Y < ymin) ymin = p.Y;
                if (p.X > xmax) xmax = p.X;
                if (p.Y > ymax) ymax = p.Y;
            }
        }
        if (double.IsInfinity(xmin)) {
            throw new InvalidOperationException(
                "SketchCircularPattern.ComputeSeedsGeometricCenter: seeds had no addressable points");
        }
        return ((xmin + xmax) / 2, (ymin + ymax) / 2);
    }

    // Same indexing convention as SketchLinearPattern.MapInstancesToSeeds, but with
    // one extra wrinkle: CreateCircularSketchStepAndRepeat ALSO appends a center
    // SketchPoint at the tail of the points array. Exclude that last point by
    // treating point-typed expectations as `expected` while consuming `expected`
    // entries starting at `prePointCount` (the +1 trailing center point sits past
    // the consumed range).
    //
    // Bookkeeping uses group ids (line/arc/point/...) not concrete RCW types —
    // pre-existing entities returned by GetSketchSegments are wrapped as the
    // generic SketchSegmentClass RCW while freshly-created pattern instances are
    // wrapped as the specific RCW (SketchLineClass etc.), so comparing GetType()
    // can never match. See SketchLinearPattern.GetGroupId.
    private static List<List<object>> MapInstancesToSeedsWithCenterPoint(
            Sketch sketch, List<object> seeds,
            Dictionary<int, int> preSegByGroup, int prePointCount,
            int instancesPerSeed) {
        var seedGroupBuckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < seeds.Count; i++) {
            var g = SketchLinearPattern.GetGroupId(seeds[i]);
            if (!seedGroupBuckets.TryGetValue(g, out var list)) {
                list = new List<int>();
                seedGroupBuckets[g] = list;
            }
            list.Add(i);
        }

        var result = new List<List<object>>(seeds.Count);
        for (int i = 0; i < seeds.Count; i++) {
            var slot = new List<object>(instancesPerSeed);
            for (int j = 0; j < instancesPerSeed; j++) slot.Add(null!);
            result.Add(slot);
        }

        var segmentsArr = (sketch.GetSketchSegments() as object[]) ?? Array.Empty<object>();
        var pointsArr = (sketch.GetSketchPoints2() as object[]) ?? Array.Empty<object>();

        foreach (var (groupId, seedIndices) in seedGroupBuckets) {
            int seedCountForGroup = seedIndices.Count;
            int expected = seedCountForGroup * instancesPerSeed;
            if (groupId == 5 /* SketchLinearPattern.GroupPoint */) {
                int newPoints = pointsArr.Length - prePointCount;
                // Circular pattern adds a trailing center SketchPoint we must skip.
                if (newPoints < expected + 1) {
                    throw new InvalidOperationException(
                        $"SketchCircularPattern.MapInstancesToSeedsWithCenterPoint: expected at least " +
                        $"{expected + 1} new points (incl. center indicator); got {newPoints}");
                }
                AssignChunks(seedIndices, instancesPerSeed, pointsArr, prePointCount, result);
            } else {
                var matching = new List<object>();
                foreach (var seg in segmentsArr) {
                    if (SketchLinearPattern.GetGroupId(seg) == groupId) matching.Add(seg);
                }
                int preCount = preSegByGroup.GetValueOrDefault(groupId, 0);
                int newCount = matching.Count - preCount;
                if (newCount < expected) {
                    throw new InvalidOperationException(
                        $"SketchCircularPattern.MapInstancesToSeedsWithCenterPoint: expected " +
                        $"{expected} new group-{groupId} segments; got {newCount}");
                }
                AssignChunks(seedIndices, instancesPerSeed, matching, preCount, result);
            }
        }
        return result;
    }

    // Same indexing as SketchLinearPattern.AssignChunks (kept private here to avoid
    // exposing the helper publicly; both composites use the same SW append-order
    // convention).
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

    private static double NormalizeAngle(double a) {
        a %= 2 * Math.PI;
        if (a < 0) a += 2 * Math.PI;
        return a;
    }
}
