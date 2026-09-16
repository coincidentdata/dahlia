using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;

namespace Sldworks.Core.Handlers.Sketches.Composites;

// Unified linear+circular sketch-pattern parser.
//
// SW emits Patterned constraints in source order, with each pattern's
// (seed -> instance) constraints forming a contiguous sub-array per seed.
// A naive "group by seed, fit each group as linear/circular independently"
// approach mis-handles two situations:
//
//   1. Auxiliary 1-instance Patterned chains (e.g. SW's internal angle-
//      indicator construction lines for a circular pattern) trivially fit
//      as 1D linear patterns — a single delta is always "linear" — so a
//      linear-first/eager parser produces spurious composites.
//   2. A real circular pattern's instance entities get consumed by the
//      spurious linear fit, leaving the circular parser with deleted=true
//      slots where SW originally had live instances.
//
// Algorithm: walk per seed in source order; for each seed, greedily peel off
// the next pattern from the front of its constraint list by computing BOTH a
// linear fit and a circular fit, picking the longer (circular wins ties —
// `circular > linear` strict). After a fit consumes its instances, those
// instance entities are immediately marked consumed in the sketch's live
// entity index, so a later seed's fit checks (per-instance "is this still
// live") see the truth. Per-seed fits with NO live instance after that update
// are dropped (the containsNonDeleted=false check).
//
// The seed entity itself is NOT consumed — only its instances. The seed line
// stays as a phase-1 entry in entitiesArr so the target can re-create it
// during AddSketch; the SketchLinearPattern/SketchCircularPattern composite
// then references the seed by id during phase-2 add.
internal static class PatternParser {
    private const string KindPatterned = nameof(ConstraintKind.Patterned);
    private const double DeltaTolerance = 1e-6;

    internal static void Detect(JsonArray entitiesArr, JsonArray constraintsArr) {
        var patternedRels = CollectPatterned(constraintsArr);
        if (patternedRels.Count == 0) return;

        // Drop Patterned relations that sit in a closed dependency cycle — they can
        // never be replayed. A pattern's seed must EXIST when the pattern is added, so
        // the seed has to be reachable from an "anchored" seed (a line that is a seed
        // but never itself an instance). A closed ring — e.g. a regular polygon SW
        // built from an internal circular pattern that PolygonParser didn't fold — has
        // NO anchored seed (every edge is both a seed and the next edge's instance), so
        // the whole ring is unsatisfiable: whatever is added first references a line a
        // later pattern still has to create. Leave those edges as raw phase-1 lines
        // (they re-create the shape directly); StripLeftoverPatternedConstraints then
        // clears the orphaned Patterned constraints.
        var cyclicConstraintIdx = FindCyclicPatternRels(patternedRels);

        // Live-entity index (id -> position in entitiesArr). We mutate this as
        // each accepted fit consumes its instances, so subsequent per-seed
        // ParseSinglePattern calls see consumed instances as "deleted".
        var entityIndexById = BuildEntityIndex(entitiesArr);

        // Group by seed in seed-discovery order. Skip seeds whose entity isn't
        // in entitiesArr (already consumed by an earlier composite parser like
        // PolygonParser) and relations left raw as part of an unsatisfiable cycle.
        var seedOrder = new List<EntityKey>();
        var seedToList = new Dictionary<EntityKey, List<PatternedRel>>();
        foreach (var rel in patternedRels) {
            if (cyclicConstraintIdx.Contains(rel.ConstraintIdx)) continue;
            var seedKey = new EntityKey(rel.SeedKind, rel.SeedId);
            if (!entityIndexById.ContainsKey(seedKey)) continue;
            if (!seedToList.TryGetValue(seedKey, out var bucket)) {
                bucket = new List<PatternedRel>();
                seedToList[seedKey] = bucket;
                seedOrder.Add(seedKey);
            }
            bucket.Add(rel);
        }

        var perSeedFits = new List<SeedFit>();
        var consumedConstraintIndices = new HashSet<int>();
        // Track instance entities consumed by accepted fits at acceptance time —
        // we can't recover this list later from entityIndexById because we drain
        // that map below to drive the all-deleted check for subsequent per-seed
        // fits. Seeds are NOT added here — they stay as phase-1 entities.
        var consumedInstanceKeys = new HashSet<EntityKey>();

        foreach (var seedKey in seedOrder) {
            var rels = seedToList[seedKey];
            while (rels.Count > 0) {
                var fit = ParseSinglePattern(seedKey, rels, entitiesArr, entityIndexById);
                if (fit is null) {
                    // Orphan (neither linear nor circular fits >= 2). Drop the
                    // front constraint and continue — leftover Patterned cleanup
                    // would handle it later, but doing it here keeps the trace
                    // self-consistent (consumed indices include all the rels we
                    // touched, not just the accepted prefix).
                    consumedConstraintIndices.Add(rels[0].ConstraintIdx);
                    rels.RemoveAt(0);
                    continue;
                }

                // All-instances-deleted check: if no instance in this fit is
                // live (i.e. each instance was already consumed by an earlier
                // fit), skip the composite emission BUT still consume the
                // constraints so leftover-Patterned cleanup doesn't see ghost
                // relations to removed entities.
                bool anyLive = false;
                foreach (var (instKey, _, _) in fit.Instances) {
                    if (entityIndexById.ContainsKey(instKey)) { anyLive = true; break; }
                }
                if (!anyLive) {
                    foreach (var (_, _, ci) in fit.Instances) {
                        consumedConstraintIndices.Add(ci);
                    }
                    rels.RemoveRange(0, fit.Consumed);
                    continue;
                }

                // Accept the fit. Mark instances consumed in entityIndexById so
                // subsequent ParseSinglePattern calls see them as deleted. Also
                // record them in consumedInstanceKeys so the entities-removal
                // walk below can find them (entityIndexById will be drained by
                // the time we get there). Seeds STAY in entityIndexById — they
                // remain phase-1 entities that the target re-creates before
                // phase-2 add adds the pattern.
                foreach (var (instKey, _, ci) in fit.Instances) {
                    consumedConstraintIndices.Add(ci);
                    entityIndexById.Remove(instKey);
                    consumedInstanceKeys.Add(instKey);
                }
                rels.RemoveRange(0, fit.Consumed);
                perSeedFits.Add(fit);
            }
        }

        if (perSeedFits.Count == 0 && consumedConstraintIndices.Count == 0) return;

        // Group per-seed fits by matching params into multi-seed composites.
        var linearGroups = MergeLinearFits(perSeedFits, entityIndexById);
        var circularGroups = MergeCircularFits(perSeedFits, entityIndexById);

        var newComposites = new List<JsonObject>();
        // Start from the per-fit-accept consumed list; the merge-and-build step
        // doesn't add any new entity removals beyond those + the center point.
        var consumedEntityKeys = new HashSet<EntityKey>(consumedInstanceKeys);

        foreach (var grp in linearGroups) {
            var node = BuildLinearPatternNode(entitiesArr, entityIndexById, grp);
            if (node is null) continue;
            newComposites.Add(node);
        }
        foreach (var grp in circularGroups) {
            var node = BuildCircularPatternNode(entitiesArr, entityIndexById, grp);
            if (node is null) continue;
            newComposites.Add(node);
            // SW's circular sketch pattern produces a standalone center
            // SketchPoint as a side effect; if present, the composite owns it
            // (SketchCircularPattern.Add re-creates it from the `center` field).
            var centerKey = FindCenterPointKey(entitiesArr, grp.CenterX, grp.CenterY);
            if (centerKey is not null) consumedEntityKeys.Add(centerKey.Value);
        }

        // Remove consumed entities (descending index walk).
        var indicesToRemove = new List<int>();
        for (int i = 0; i < entitiesArr.Count; i++) {
            var node = entitiesArr[i] as JsonObject;
            var idObj = node?["id"] as JsonObject;
            var idLong = idObj?["id"]?.GetValue<long>();
            var kind = idObj?["entity_kind"]?.GetValue<string>();
            if (idLong is not null && kind is not null
                && consumedEntityKeys.Contains(new EntityKey(kind, idLong.Value))) {
                indicesToRemove.Add(i);
            }
        }
        for (int k = indicesToRemove.Count - 1; k >= 0; k--) {
            entitiesArr.RemoveAt(indicesToRemove[k]);
        }
        foreach (var node in newComposites) entitiesArr.Add(node);

        var consumedSorted = consumedConstraintIndices.OrderBy(x => x).ToList();
        for (int k = consumedSorted.Count - 1; k >= 0; k--) {
            constraintsArr.RemoveAt(consumedSorted[k]);
        }
    }

    // ---- per-seed pattern picker --------------------------------------------

    private static SeedFit? ParseSinglePattern(
            EntityKey seedKey,
            List<PatternedRel> rels,
            JsonArray entitiesArr,
            Dictionary<EntityKey, int> entityIndexById) {
        if (rels.Count == 0) return null;

        // Prefer the live seed entity in entitiesArr (post-MergePoints geometry)
        // over the constraint ref's geometry. They agree for non-fused entities;
        // for the seed itself (which is never deleted from the sketch) entitiesArr
        // is canonical.
        (double X, double Y)? seedAnchor = null;
        if (entityIndexById.TryGetValue(seedKey, out var seedIdx)) {
            seedAnchor = AnchorPoint(entitiesArr[seedIdx]);
        }
        seedAnchor ??= AnchorPoint(rels[0].SeedRef);
        if (seedAnchor is null) return null;

        var anchors = new List<(double X, double Y)>(rels.Count);
        foreach (var rel in rels) {
            var a = AnchorPoint(rel.InstanceRef);
            if (a is null) break;
            anchors.Add(a.Value);
        }
        if (anchors.Count == 0) return null;

        var deltas = new List<(double DX, double DY)>(anchors.Count);
        foreach (var a in anchors) {
            deltas.Add((a.X - seedAnchor.Value.X, a.Y - seedAnchor.Value.Y));
        }

        var linearFit = FitLinearGrid(deltas);
        var circularFit = FitCircularArc(seedAnchor.Value, anchors);

        int linearTotal = linearFit is null ? 0 : linearFit.Value.CountA * linearFit.Value.CountB;
        int circularTotal = circularFit?.Count ?? 0;

        if (linearTotal <= 1 && circularTotal <= 1) return null;

        // Pick the longer; circular wins ONLY when strictly greater.
        // Equal-size ties go to linear (`circularSize > linearSize`).
        if (circularTotal > linearTotal) {
            int consumed = circularTotal - 1;
            var instances = new List<(EntityKey Key, JsonObject Ref, int ConstraintIdx)>(consumed);
            for (int k = 0; k < consumed; k++) {
                instances.Add((new EntityKey(rels[k].InstanceKind, rels[k].InstanceId),
                               rels[k].InstanceRef, rels[k].ConstraintIdx));
            }
            return new SeedFit(
                Kind: FitKind.Circular,
                SeedKey: seedKey,
                Consumed: consumed,
                AngleA: 0, SpacingA: 0, CountA: 0,
                AngleB: 0, SpacingB: 0, CountB: 0,
                CenterX: circularFit!.Value.CenterX, CenterY: circularFit.Value.CenterY,
                AngleStep: circularFit.Value.AngleStep, Count: circularFit.Value.Count,
                Instances: instances);
        } else {
            int consumed = linearTotal - 1;
            var instances = new List<(EntityKey Key, JsonObject Ref, int ConstraintIdx)>(consumed);
            for (int k = 0; k < consumed; k++) {
                instances.Add((new EntityKey(rels[k].InstanceKind, rels[k].InstanceId),
                               rels[k].InstanceRef, rels[k].ConstraintIdx));
            }
            return new SeedFit(
                Kind: FitKind.Linear,
                SeedKey: seedKey,
                Consumed: consumed,
                AngleA: linearFit!.Value.AngleA, SpacingA: linearFit.Value.SpacingA,
                CountA: linearFit.Value.CountA,
                AngleB: linearFit.Value.AngleB, SpacingB: linearFit.Value.SpacingB,
                CountB: linearFit.Value.CountB,
                CenterX: 0, CenterY: 0, AngleStep: 0, Count: 0,
                Instances: instances);
        }
    }

    // ---- shape fitters ------------------------------------------------------

    private static (double AngleA, double SpacingA, int CountA,
                    double AngleB, double SpacingB, int CountB)?
        FitLinearGrid(List<(double DX, double DY)> deltas) {
        if (deltas.Count == 0) return null;

        var (ax, ay) = deltas[0];
        var spacingA = Math.Sqrt(ax * ax + ay * ay);
        if (spacingA < DeltaTolerance) return null;
        var angleA = Math.Atan2(ay, ax);

        // Walk along A: each delta[k] should equal (k+1) * (ax, ay).
        int countA1d = 1;
        for (int k = 1; k < deltas.Count; k++) {
            var expectedX = (k + 1) * ax;
            var expectedY = (k + 1) * ay;
            if (Approximately(deltas[k].DX, expectedX) && Approximately(deltas[k].DY, expectedY)) {
                countA1d++;
            } else {
                break;
            }
        }
        if (countA1d == deltas.Count) {
            return (angleA, spacingA, countA1d + 1, 0.0, 0.0, 1);
        }

        // 2D fit: the next delta starts row 1, defining direction-B.
        int countA = countA1d + 1;
        int idx = countA1d;
        if (idx >= deltas.Count) return null;
        var (bx, by) = deltas[idx];
        var spacingB = Math.Sqrt(bx * bx + by * by);
        if (spacingB < DeltaTolerance) return null;
        var angleB = Math.Atan2(by, bx);

        int rowStart = idx;
        int rowsCounted = 1;
        for (;;) {
            for (int col = 1; col < countA; col++) {
                int pos = rowStart + col;
                if (pos >= deltas.Count) return null;
                var expX = col * ax + rowsCounted * bx;
                var expY = col * ay + rowsCounted * by;
                if (!Approximately(deltas[pos].DX, expX) || !Approximately(deltas[pos].DY, expY)) {
                    return null;
                }
            }
            rowStart += countA;
            rowsCounted++;
            if (rowStart >= deltas.Count) break;
            var nextExpX = rowsCounted * bx;
            var nextExpY = rowsCounted * by;
            if (!Approximately(deltas[rowStart].DX, nextExpX) || !Approximately(deltas[rowStart].DY, nextExpY)) {
                return null;
            }
        }
        int countB = rowsCounted + 1;
        if (deltas.Count != countA * countB - 1) return null;
        return (angleA, spacingA, countA, angleB, spacingB, countB);
    }

    private static (double CenterX, double CenterY, double AngleStep, int Count)?
        FitCircularArc(
            (double X, double Y) seed,
            List<(double X, double Y)> instances) {
        if (instances.Count < 2) return null;
        var p0 = (seed.X, seed.Y);
        var p1 = (instances[0].X, instances[0].Y);
        var p2 = (instances[1].X, instances[1].Y);
        if (!CircleCenterFromThreePoints(p0, p1, p2, out double cx, out double cy)) return null;

        double radius = Distance((cx, cy), p0);
        if (radius < DeltaTolerance) return null;

        double angle0 = Math.Atan2(p0.Y - cy, p0.X - cx);
        double angle1 = Math.Atan2(p1.Y - cy, p1.X - cx);
        double step = WrapAngle(angle1 - angle0);
        if (Math.Abs(step) < DeltaTolerance) return null;

        // Find the longest leading prefix that lies on the same circle at the
        // same angular step. Extend greedily from the front — break at the
        // first mismatch and return that count.
        int matched = 1;  // instances[0] consumed by the center solve
        for (int k = 1; k < instances.Count; k++) {
            var (x, y) = instances[k];
            double rk = Distance((cx, cy), (x, y));
            if (Math.Abs(rk - radius) > DeltaTolerance) break;
            double angleK = Math.Atan2(y - cy, x - cx);
            double expected = angle0 + step * (k + 1);
            if (Math.Abs(WrapAngle(angleK - expected)) > DeltaTolerance) break;
            matched++;
        }
        return (cx, cy, step, matched + 1);  // +1 for the seed at position 0
    }

    private static bool CircleCenterFromThreePoints(
            (double X, double Y) a, (double X, double Y) b, (double X, double Y) c,
            out double cx, out double cy) {
        cx = cy = 0;
        double ax = a.X, ay = a.Y;
        double bx = b.X, by = b.Y;
        double cxp = c.X, cyp = c.Y;
        double d = 2 * (ax * (by - cyp) + bx * (cyp - ay) + cxp * (ay - by));
        if (Math.Abs(d) < 1e-12) return false;
        double ux = ((ax * ax + ay * ay) * (by - cyp)
                  + (bx * bx + by * by) * (cyp - ay)
                  + (cxp * cxp + cyp * cyp) * (ay - by)) / d;
        double uy = ((ax * ax + ay * ay) * (cxp - bx)
                  + (bx * bx + by * by) * (ax - cxp)
                  + (cxp * cxp + cyp * cyp) * (bx - ax)) / d;
        cx = ux; cy = uy;
        return true;
    }

    // ---- fit grouping (multi-seed composites) -------------------------------

    private static List<LinearFitGroup> MergeLinearFits(
            List<SeedFit> fits, Dictionary<EntityKey, int> entityIndexById) {
        var groups = new List<LinearFitGroup>();
        foreach (var fit in fits) {
            if (fit.Kind != FitKind.Linear) continue;
            LinearFitGroup? match = null;
            foreach (var g in groups) {
                if (g.CountA != fit.CountA || g.CountB != fit.CountB) continue;
                if (Math.Abs(g.SpacingA - fit.SpacingA) > DeltaTolerance) continue;
                if (Math.Abs(WrapAngle(g.AngleA - fit.AngleA)) > DeltaTolerance) continue;
                if (g.CountB > 1) {
                    if (Math.Abs(g.SpacingB - fit.SpacingB) > DeltaTolerance) continue;
                    if (Math.Abs(WrapAngle(g.AngleB - fit.AngleB)) > DeltaTolerance) continue;
                }
                if (g.SeedIds.Contains(fit.SeedKey)) continue;
                if (g.AllLiveInstanceIds.Contains(fit.SeedKey)) continue;
                if (fit.Instances.Any(t => g.SeedIds.Contains(t.Key))) continue;
                match = g; break;
            }
            if (match is null) {
                match = new LinearFitGroup {
                    AngleA = fit.AngleA, SpacingA = fit.SpacingA, CountA = fit.CountA,
                    AngleB = fit.AngleB, SpacingB = fit.SpacingB, CountB = fit.CountB,
                };
                groups.Add(match);
            }
            match.SeedIds.Add(fit.SeedKey);
            var perSeed = new List<(EntityKey Key, JsonObject Ref)>(fit.Instances.Count);
            foreach (var (key, refObj, _) in fit.Instances) {
                perSeed.Add((key, refObj));
                if (entityIndexById.ContainsKey(key)) {
                    match.AllLiveInstanceIds.Add(key);
                }
            }
            match.InstancesBySeed.Add(perSeed);
        }
        return groups;
    }

    private static List<CircularFitGroup> MergeCircularFits(
            List<SeedFit> fits, Dictionary<EntityKey, int> entityIndexById) {
        var groups = new List<CircularFitGroup>();
        foreach (var fit in fits) {
            if (fit.Kind != FitKind.Circular) continue;
            CircularFitGroup? match = null;
            foreach (var g in groups) {
                if (g.Count != fit.Count) continue;
                if (Math.Abs(g.CenterX - fit.CenterX) > DeltaTolerance) continue;
                if (Math.Abs(g.CenterY - fit.CenterY) > DeltaTolerance) continue;
                if (Math.Abs(WrapAngle(g.AngleStep - fit.AngleStep)) > DeltaTolerance) continue;
                if (g.SeedIds.Contains(fit.SeedKey)) continue;
                if (g.AllLiveInstanceIds.Contains(fit.SeedKey)) continue;
                if (fit.Instances.Any(t => g.SeedIds.Contains(t.Key))) continue;
                match = g; break;
            }
            if (match is null) {
                match = new CircularFitGroup {
                    CenterX = fit.CenterX, CenterY = fit.CenterY,
                    AngleStep = fit.AngleStep, Count = fit.Count,
                };
                groups.Add(match);
            }
            match.SeedIds.Add(fit.SeedKey);
            var perSeed = new List<(EntityKey Key, JsonObject Ref)>(fit.Instances.Count);
            foreach (var (key, refObj, _) in fit.Instances) {
                perSeed.Add((key, refObj));
                if (entityIndexById.ContainsKey(key)) {
                    match.AllLiveInstanceIds.Add(key);
                }
            }
            match.InstancesBySeed.Add(perSeed);
        }
        return groups;
    }

    // ---- composite node builders --------------------------------------------

    private static JsonObject? BuildLinearPatternNode(
            JsonArray entitiesArr,
            Dictionary<EntityKey, int> entityIndexById,
            LinearFitGroup group) {
        var seedsArr = new JsonArray();
        var entitiesPerSeedArr = new JsonArray();
        var deletedPerSeedArr = new JsonArray();
        bool construction = false;

        for (int s = 0; s < group.SeedIds.Count; s++) {
            var seedKey = group.SeedIds[s];
            // Look up the seed's id triplet from the live entity (still in entitiesArr —
            // we don't remove seeds during parse).
            var seedNode = LookupEntityByKey(entitiesArr, seedKey);
            var seedIdNode = seedNode?["id"] as JsonObject;
            if (seedIdNode is null) return null;
            seedsArr.Add(seedIdNode.DeepClone());
            if (s == 0) construction = seedNode!["construction"]?.GetValue<bool>() ?? false;

            var instArr = new JsonArray();
            var delArr = new JsonArray();
            foreach (var (instKey, instRef) in group.InstancesBySeed[s]) {
                var liveNode = LookupEntityByKey(entitiesArr, instKey);
                if (liveNode is not null) {
                    instArr.Add(liveNode.DeepClone());
                    delArr.Add(false);
                } else {
                    // Instance no longer in entitiesArr — either user-deleted it
                    // from the pattern, or an earlier fit's instance-removal got
                    // here first (and the all-deleted check above didn't drop us
                    // because at least one OTHER instance of this group is live).
                    instArr.Add(instRef.DeepClone());
                    delArr.Add(true);
                }
            }
            entitiesPerSeedArr.Add(instArr);
            deletedPerSeedArr.Add(delArr);
        }

        return new JsonObject {
            ["type"]         = SketchLinearPattern.TypeName,
            ["seeds"]        = seedsArr,
            ["angle_a"]      = group.AngleA,
            ["spacing_a"]    = group.SpacingA,
            ["count_a"]      = group.CountA,
            ["angle_b"]      = group.AngleB,
            ["spacing_b"]    = group.SpacingB,
            ["count_b"]      = group.CountB,
            ["construction"] = construction,
            ["entities"]     = entitiesPerSeedArr,
            ["deleted"]      = deletedPerSeedArr,
        };
    }

    private static JsonObject? BuildCircularPatternNode(
            JsonArray entitiesArr,
            Dictionary<EntityKey, int> entityIndexById,
            CircularFitGroup group) {
        var seedsArr = new JsonArray();
        var entitiesPerSeedArr = new JsonArray();
        var deletedPerSeedArr = new JsonArray();
        bool construction = false;

        for (int s = 0; s < group.SeedIds.Count; s++) {
            var seedKey = group.SeedIds[s];
            var seedNode = LookupEntityByKey(entitiesArr, seedKey);
            var seedIdNode = seedNode?["id"] as JsonObject;
            if (seedIdNode is null) return null;
            seedsArr.Add(seedIdNode.DeepClone());
            if (s == 0) construction = seedNode!["construction"]?.GetValue<bool>() ?? false;

            var instArr = new JsonArray();
            var delArr = new JsonArray();
            foreach (var (instKey, instRef) in group.InstancesBySeed[s]) {
                var liveNode = LookupEntityByKey(entitiesArr, instKey);
                if (liveNode is not null) {
                    instArr.Add(liveNode.DeepClone());
                    delArr.Add(false);
                } else {
                    instArr.Add(instRef.DeepClone());
                    delArr.Add(true);
                }
            }
            entitiesPerSeedArr.Add(instArr);
            deletedPerSeedArr.Add(delArr);
        }

        return new JsonObject {
            ["type"]         = SketchCircularPattern.TypeName,
            ["seeds"]        = seedsArr,
            ["center"]       = new JsonObject { ["x"] = group.CenterX, ["y"] = group.CenterY },
            ["angle_step"]   = group.AngleStep,
            ["count"]        = group.Count,
            ["construction"] = construction,
            ["entities"]     = entitiesPerSeedArr,
            ["deleted"]      = deletedPerSeedArr,
        };
    }

    // ---- helpers ------------------------------------------------------------

    private static List<PatternedRel> CollectPatterned(JsonArray constraintsArr) {
        var list = new List<PatternedRel>();
        for (int i = 0; i < constraintsArr.Count; i++) {
            var node = constraintsArr[i] as JsonObject;
            if (node?["kind"]?.GetValue<string>() != KindPatterned) continue;
            var refs = node["refs"] as JsonArray;
            if (refs is null || refs.Count < 2) continue;
            var seedRef = refs[0] as JsonObject;
            var instRef = refs[1] as JsonObject;
            var seedId = seedRef?["id"]?["id"]?.GetValue<long>();
            var instId = instRef?["id"]?["id"]?.GetValue<long>();
            var seedKind = seedRef?["id"]?["entity_kind"]?.GetValue<string>();
            var instKind = instRef?["id"]?["entity_kind"]?.GetValue<string>();
            if (seedRef is null || instRef is null
                || seedId is null || instId is null
                || seedKind is null || instKind is null) continue;
            list.Add(new PatternedRel(i, seedKind, seedId.Value, instKind, instId.Value, seedRef, instRef));
        }
        return list;
    }

    // Patterned relations whose seed can't be built on replay because it sits in a
    // closed dependency cycle. A seed is "anchored" if it is never any relation's
    // instance; buildability spreads transitively along seed→instance edges. Any
    // relation whose seed never becomes buildable is cyclic. See Detect for the why.
    private static HashSet<int> FindCyclicPatternRels(List<PatternedRel> rels) {
        var instanceKeys = new HashSet<EntityKey>();
        foreach (var r in rels) {
            instanceKeys.Add(new EntityKey(r.InstanceKind, r.InstanceId));
        }
        // Seed anchors: seeds that are never themselves an instance.
        var buildable = new HashSet<EntityKey>();
        foreach (var r in rels) {
            var seedKey = new EntityKey(r.SeedKind, r.SeedId);
            if (!instanceKeys.Contains(seedKey)) buildable.Add(seedKey);
        }
        // Spread buildability: a relation whose seed is buildable makes its instance buildable.
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (var r in rels) {
                if (!buildable.Contains(new EntityKey(r.SeedKind, r.SeedId))) continue;
                if (buildable.Add(new EntityKey(r.InstanceKind, r.InstanceId))) changed = true;
            }
        }
        var cyclic = new HashSet<int>();
        foreach (var r in rels) {
            if (!buildable.Contains(new EntityKey(r.SeedKind, r.SeedId))) {
                cyclic.Add(r.ConstraintIdx);
            }
        }
        return cyclic;
    }

    private static Dictionary<EntityKey, int> BuildEntityIndex(JsonArray entitiesArr) {
        var map = new Dictionary<EntityKey, int>();
        for (int i = 0; i < entitiesArr.Count; i++) {
            var node = entitiesArr[i] as JsonObject;
            var idObj = node?["id"] as JsonObject;
            var idLong = idObj?["id"]?.GetValue<long>();
            var kind = idObj?["entity_kind"]?.GetValue<string>();
            if (idLong is not null && kind is not null) {
                map[new EntityKey(kind, idLong.Value)] = i;
            }
        }
        return map;
    }

    // Lookup-by-(kind,id) over entitiesArr. Linear scan; fine for sketch sizes.
    private static JsonObject? LookupEntityByKey(JsonArray entitiesArr, EntityKey key) {
        for (int i = 0; i < entitiesArr.Count; i++) {
            var node = entitiesArr[i] as JsonObject;
            var idObj = node?["id"] as JsonObject;
            var idLong = idObj?["id"]?.GetValue<long>();
            var kind = idObj?["entity_kind"]?.GetValue<string>();
            if (idLong == key.Id && kind == key.Kind) return node;
        }
        return null;
    }

    // Anchor point per entity flavor — same convention for both seed and
    // instance so deltas / fits are invariant.
    private static (double X, double Y)? AnchorPoint(JsonNode? entityNode) {
        if (entityNode is not JsonObject obj) return null;
        var kind = obj["kind"]?.GetValue<string>();
        switch (kind) {
            case "sketch_line": {
                var sx = obj["start"]?["p"]?["x"]?.GetValue<double>();
                var sy = obj["start"]?["p"]?["y"]?.GetValue<double>();
                var ex = obj["end"]?["p"]?["x"]?.GetValue<double>();
                var ey = obj["end"]?["p"]?["y"]?.GetValue<double>();
                if (sx is null || sy is null || ex is null || ey is null) return null;
                return ((sx.Value + ex.Value) / 2, (sy.Value + ey.Value) / 2);
            }
            case "sketch_circle":
            case "sketch_arc":
            case "sketch_ellipse":
            case "sketch_elliptical_arc": {
                var x = obj["center"]?["p"]?["x"]?.GetValue<double>();
                var y = obj["center"]?["p"]?["y"]?.GetValue<double>();
                if (x is null || y is null) return null;
                return (x.Value, y.Value);
            }
            case "sketch_parabola": {
                var x = obj["apex"]?["p"]?["x"]?.GetValue<double>();
                var y = obj["apex"]?["p"]?["y"]?.GetValue<double>();
                if (x is null || y is null) return null;
                return (x.Value, y.Value);
            }
            case "sketch_spline": {
                var cps = obj["control_points"] as JsonArray;
                var first = cps?.FirstOrDefault() as JsonObject;
                var x = first?["x"]?.GetValue<double>();
                var y = first?["y"]?.GetValue<double>();
                if (x is null || y is null) return null;
                return (x.Value, y.Value);
            }
            case "sketch_point": {
                var x = obj["p"]?["x"]?.GetValue<double>();
                var y = obj["p"]?["y"]?.GetValue<double>();
                if (x is null || y is null) return null;
                return (x.Value, y.Value);
            }
            default:
                return null;
        }
    }

    private static EntityKey? FindCenterPointKey(JsonArray entitiesArr, double cx, double cy) {
        for (int i = 0; i < entitiesArr.Count; i++) {
            var node = entitiesArr[i] as JsonObject;
            if (node?["kind"]?.GetValue<string>() != "sketch_point") continue;
            var px = node["p"]?["x"]?.GetValue<double>();
            var py = node["p"]?["y"]?.GetValue<double>();
            if (px is null || py is null) continue;
            if (Math.Abs(px.Value - cx) <= DeltaTolerance
                && Math.Abs(py.Value - cy) <= DeltaTolerance) {
                var idObj = node["id"] as JsonObject;
                var idLong = idObj?["id"]?.GetValue<long>();
                var kind = idObj?["entity_kind"]?.GetValue<string>();
                if (idLong is not null && kind is not null) {
                    return new EntityKey(kind, idLong.Value);
                }
            }
        }
        return null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double WrapAngle(double a) {
        a %= 2 * Math.PI;
        if (a >= Math.PI) a -= 2 * Math.PI;
        if (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private static bool Approximately(double a, double b) =>
        Math.Abs(a - b) < DeltaTolerance;

    // ---- internal types -----------------------------------------------------

    private enum FitKind { Linear, Circular }

    private readonly record struct PatternedRel(
        int ConstraintIdx,
        string SeedKind, long SeedId,
        string InstanceKind, long InstanceId,
        JsonObject SeedRef, JsonObject InstanceRef);

    // Composite key for entity lookup. SW's GetID() namespace is per-COM-subtype:
    // a SketchArc and a SketchLine in the same sketch can both have id=1. Using
    // bare long ids would conflate them and let the parser pick the wrong entity
    // as a pattern seed (a SW circular sketch pattern emits guideline ARCS at
    // ids that COLLIDE with the line seed ids — without (kind, id) keying we
    // pick the arc and the produced composite refs the wrong entity).
    private readonly record struct EntityKey(string Kind, long Id);

    private record SeedFit(
        FitKind Kind,
        EntityKey SeedKey,
        int Consumed,
        double AngleA, double SpacingA, int CountA,
        double AngleB, double SpacingB, int CountB,
        double CenterX, double CenterY, double AngleStep, int Count,
        List<(EntityKey Key, JsonObject Ref, int ConstraintIdx)> Instances);

    private sealed class LinearFitGroup {
        public List<EntityKey> SeedIds = new();
        public List<List<(EntityKey Key, JsonObject Ref)>> InstancesBySeed = new();
        public List<EntityKey> AllLiveInstanceIds = new();
        public double AngleA, SpacingA, AngleB, SpacingB;
        public int CountA, CountB;
    }

    private sealed class CircularFitGroup {
        public List<EntityKey> SeedIds = new();
        public List<List<(EntityKey Key, JsonObject Ref)>> InstancesBySeed = new();
        public List<EntityKey> AllLiveInstanceIds = new();
        public double CenterX, CenterY, AngleStep;
        public int Count;
    }
}
