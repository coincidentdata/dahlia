using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;

namespace Sldworks.Core.Handlers.Sketches.Composites;

// Inspect-side polygon reconstruction. Walks the constraints array looking for the
// SW polygon signature — N consecutive Patterned relations chaining edge[0] → edge[1]
// → ... → edge[0] (a closed ring) plus a Tangent (or any other circle-touching) relation
// linking one of the polygon's edges to a construction circle whose center sits on the
// polygon's centroid. When found, replaces the construction circle entry in `entitiesArr`
// with a SketchPolygon composite (carrying the edges + the circle), removes the polygon's
// edges from `entitiesArr`, and removes the consumed Patterned + linking constraints from
// `constraintsArr`. Any leftover Patterned constraints that didn't match a polygon ring
// are stripped at the end since Patterned is internal-only and never authored.
internal static class PolygonParser {
    private const string KindPatterned       = nameof(ConstraintKind.Patterned);
    private const string KindSketchLine      = "sketch_line";
    private const string KindSketchCircle    = "sketch_circle";
    private const string KindSketchPolygon   = "Polygon";

    // Centroid coincidence tolerance — fraction of the construction-circle radius.
    // 10% (`Radius * 0.1`): the polygon centroid sits exactly at the circle center
    // for a regular polygon, so this is generous for floating noise + any sub-1mm
    // edge drift from solver rebuilds.
    private const double CenterMatchToleranceFraction = 0.1;

    internal static void Detect(JsonArray entitiesArr, JsonArray constraintsArr) {
        // Index entities by SketchEntityId so we can fetch a circle / line by id pair
        // when a constraint's ref names one. Built once up front; mutated as polygons
        // are recognized and entities are consumed.
        var entityIndexById = BuildEntityIndex(entitiesArr);

        // Scan for polygon rings. The ring detection needs CONSECUTIVE Patterned
        // constraints in the order SW emits them, so we walk constraintsArr by
        // current index. When a polygon is detected we splice out its constraints —
        // restart the scan at the same index (which now points at what was the next
        // unrelated constraint).
        for (int i = 0; i < constraintsArr.Count; ) {
            var ringEnd = FindPolygonRingEnd(constraintsArr, i);
            if (ringEnd < 0) { i++; continue; }

            // The ring spans constraints[i..ringEnd] inclusive; collect each edge in
            // chain order (constraint[k].refs[0] for k = i..ringEnd-1, plus the final
            // chain's refs[1] — which loops back to the first edge).
            var edgeIds = ExtractRingEdgeIds(constraintsArr, i, ringEnd);
            if (edgeIds is null || edgeIds.Count != ringEnd - i + 1) {
                // Malformed ring shape — skip past this Patterned and keep scanning.
                i++; continue;
            }
            // The circle-link set includes the edge LINE ids AND each edge's
            // endpoint POINT ids. Inscribed polygons link the circle to an
            // edge (Tangent → line id); circumscribed polygons link it to a
            // vertex (Coincident → endpoint point id). Including both lets us
            // detect either (line + endpoints).
            var edgeIdSet = new HashSet<long>(edgeIds);
            foreach (var id in edgeIds) {
                var edge = LookupEdgeNode(entitiesArr, entityIndexById, id);
                var sId = (edge?["start"]?["id"]?["id"])?.GetValue<long>();
                var eId = (edge?["end"]?["id"]?["id"])?.GetValue<long>();
                if (sId is not null) edgeIdSet.Add(sId.Value);
                if (eId is not null) edgeIdSet.Add(eId.Value);
            }

            // Find the constraint linking any of the polygon's edges/vertices to
            // a circle. Scan the WHOLE constraints array (not just from i onward)
            // because the link can be authored before or after the pattern chain.
            var (linkIdx, circleId) = FindCircleLinkConstraint(constraintsArr, edgeIdSet);
            if (linkIdx < 0 || circleId is null) {
                // Polygon has no associated construction circle in the constraint
                // graph — drop the pattern ring (those constraints are internal-only
                // and have no value on their own).
                RemoveRange(constraintsArr, i, ringEnd - i + 1);
                continue;
            }

            // Look up the circle entity in entitiesArr and its entries.
            if (!entityIndexById.TryGetValue(circleId.Value, out var circleEntityIdx)) {
                RemoveRange(constraintsArr, i, ringEnd - i + 1);
                continue;
            }
            var circleNode = entitiesArr[circleEntityIdx] as JsonObject;
            if (circleNode is null || circleNode["kind"]?.GetValue<string>() != KindSketchCircle) {
                RemoveRange(constraintsArr, i, ringEnd - i + 1);
                continue;
            }

            // Verify the circle's center sits at (or near) the polygon's vertex centroid.
            // Mismatch suggests this isn't actually a polygon-pattern + tangent-circle
            // grouping — it could be unrelated geometry that happens to share a Patterned
            // relation. Drop the pattern ring; leave the circle alone.
            var radius = circleNode["radius"]?.GetValue<double>() ?? 0.0;
            var centerX = circleNode["center"]?["p"]?["x"]?.GetValue<double>() ?? 0.0;
            var centerY = circleNode["center"]?["p"]?["y"]?.GetValue<double>() ?? 0.0;
            if (!CentroidMatches(entitiesArr, entityIndexById, edgeIds, centerX, centerY, radius)) {
                RemoveRange(constraintsArr, i, ringEnd - i + 1);
                continue;
            }

            // Determine inscribed vs circumscribed by comparing perimeters:
            // the polygon's perimeter exceeds the circle's
            // circumference iff the circle is inscribed (sits inside, tangent
            // to edge midpoints). This sets which construction circle SW
            // rebuilds — `first_vertex` (a corner) carries the actual rotation
            // regardless.
            var polygonPerim = ComputeRingPerimeter(entitiesArr, entityIndexById, edgeIds);
            var inscribed = polygonPerim > 2 * Math.PI * radius;

            // Build the polygon entity, replace the circle node with it, and collect the
            // edge node indices to remove afterward.
            var polygonNode = BuildPolygonNode(entitiesArr, entityIndexById, edgeIds,
                circleNode, centerX, centerY, inscribed);
            entitiesArr[circleEntityIdx] = polygonNode;

            // Remove the edge nodes from entitiesArr. Walk descending so indices stay valid.
            var edgeIndices = edgeIds
                .Select(id => entityIndexById.TryGetValue(id, out var idx) ? idx : -1)
                .Where(idx => idx >= 0 && idx != circleEntityIdx)
                .OrderByDescending(idx => idx)
                .ToList();
            foreach (var idx in edgeIndices) {
                entitiesArr.RemoveAt(idx);
            }

            // Constraints to drop: the Patterned ring (i..ringEnd) AND the circle-link
            // constraint at linkIdx. Watch the index ordering — both spans are removed
            // on the same array, so account for shifts.
            RemoveRange(constraintsArr, i, ringEnd - i + 1);
            var adjustedLinkIdx = linkIdx > ringEnd ? linkIdx - (ringEnd - i + 1) : linkIdx;
            if (adjustedLinkIdx >= 0 && adjustedLinkIdx < constraintsArr.Count) {
                constraintsArr.RemoveAt(adjustedLinkIdx);
                // RemoveRange already slid the NEXT ring's first constraint down to index i.
                // If the circle-link we just removed sat BEFORE i, that removal shifted every
                // later constraint (including that next-ring start) down one more, so i now
                // overshoots it. Step back one. Without this a SECOND concentric polygon's ring
                // is skipped, and its edges fall to PatternParser as an unsatisfiable cycle.
                if (adjustedLinkIdx < i) i--;
            }

            // entitiesArr was mutated; rebuild the id index for the next iteration.
            entityIndexById = BuildEntityIndex(entitiesArr);
            // Don't advance i — `i` now points at what was the constraint after the
            // consumed ring; keep scanning for further polygons.
        }

        // Leftover Patterned constraints are stripped by SketchHandler.Inspect after
        // all composite parsers run — PatternParser also consumes Patterned constraints,
        // so stripping here would eat the ones it depends on.
    }

    // ---- helpers ---------------------------------------------------------------

    // System.Text.Json's JsonArray exposes RemoveAt and Clear but no RemoveRange,
    // so we splice manually. Walks descending so the indices stay valid as we go.
    private static void RemoveRange(JsonArray arr, int start, int count) {
        for (int k = start + count - 1; k >= start; k--) {
            arr.RemoveAt(k);
        }
    }

    // Builds (SketchEntityId.Id long → entitiesArr index). Skips entries without an id
    // (composites already in the array, which won't be re-detected).
    private static Dictionary<long, int> BuildEntityIndex(JsonArray entitiesArr) {
        var map = new Dictionary<long, int>();
        for (int i = 0; i < entitiesArr.Count; i++) {
            var node = entitiesArr[i] as JsonObject;
            var idLong = node?["id"]?["id"]?.GetValue<long>();
            if (idLong is not null) {
                map[idLong.Value] = i;
            }
        }
        return map;
    }

    // Walks consecutive Patterned constraints starting at `start`, requiring each
    // constraint to chain refs[0] → refs[1] forward through the ring. Returns the
    // last constraint index (inclusive) when the chain closes back to the starting
    // edge, else -1.
    private static int FindPolygonRingEnd(JsonArray constraintsArr, int start) {
        if (start >= constraintsArr.Count) return -1;
        var first = constraintsArr[start] as JsonObject;
        if (first?["kind"]?.GetValue<string>() != KindPatterned) return -1;

        var (firstA, firstB) = GetRefIdPair(first);
        if (firstA is null || firstB is null) return -1;

        long startEdgeId = firstA.Value;
        long expectedNext = firstB.Value;

        int idx = start + 1;
        while (idx < constraintsArr.Count) {
            var node = constraintsArr[idx] as JsonObject;
            if (node?["kind"]?.GetValue<string>() != KindPatterned) return -1;
            var (a, b) = GetRefIdPair(node);
            if (a is null || b is null) return -1;
            if (a.Value != expectedNext) return -1;
            if (b.Value == startEdgeId) return idx;  // ring closed
            expectedNext = b.Value;
            idx++;
        }
        return -1;
    }

    // Pulls the (refs[0].id.id, refs[1].id.id) pair from a constraint node. Returns
    // (null, null) if either ref is missing or not a sketch entity definition.
    private static (long? a, long? b) GetRefIdPair(JsonObject? constraintNode) {
        var refs = constraintNode?["refs"] as JsonArray;
        if (refs is null || refs.Count < 2) return (null, null);
        var a = (refs[0] as JsonObject)?["id"]?["id"]?.GetValue<long>();
        var b = (refs[1] as JsonObject)?["id"]?["id"]?.GetValue<long>();
        return (a, b);
    }

    // Edge ids in chain order. For a ring of N edges there are N Patterned constraints
    // (edge[0]→edge[1], edge[1]→edge[2], ..., edge[N-1]→edge[0]). The edges in order
    // are: refs[0] of constraints[start..end-1], plus refs[0] of constraint[end] —
    // which equals the last edge before looping. Equivalently: refs[0] of every
    // constraint in [start..end] gives the N edges in ring order.
    private static List<long>? ExtractRingEdgeIds(JsonArray constraintsArr, int start, int end) {
        var edges = new List<long>(end - start + 1);
        for (int k = start; k <= end; k++) {
            var node = constraintsArr[k] as JsonObject;
            var a = (node?["refs"] as JsonArray)?[0] as JsonObject;
            var id = a?["id"]?["id"]?.GetValue<long>();
            if (id is null) return null;
            edges.Add(id.Value);
        }
        return edges;
    }

    // Searches the entire constraints array for a constraint whose refs include
    // exactly one of the polygon's edges and a sketch_circle (the construction circle).
    // Returns (constraint index, circle id) or (-1, null). Accepts any
    // RelationConstraint (most often Tangent, but can be Concentric / Coincident etc.
    // depending on how the circle was authored).
    private static (int idx, long? circleId) FindCircleLinkConstraint(
            JsonArray constraintsArr, HashSet<long> edgeIds) {
        for (int i = 0; i < constraintsArr.Count; i++) {
            var node = constraintsArr[i] as JsonObject;
            var kind = node?["kind"]?.GetValue<string>();
            // Skip the Patterned ring constraints themselves.
            if (kind == KindPatterned) continue;
            var refs = node?["refs"] as JsonArray;
            if (refs is null) continue;

            long? circleId = null;
            bool touchesEdge = false;
            foreach (var refNode in refs) {
                var refObj = refNode as JsonObject;
                var refKind = refObj?["kind"]?.GetValue<string>();
                var refId = refObj?["id"]?["id"]?.GetValue<long>();
                if (refId is null) continue;
                if (refKind == KindSketchCircle) {
                    circleId = refId;
                } else if (edgeIds.Contains(refId.Value)) {
                    touchesEdge = true;
                }
            }
            if (touchesEdge && circleId is not null) {
                return (i, circleId);
            }
        }
        return (-1, null);
    }

    // True if the polygon's vertex centroid (mean of edge.start coords) sits within
    // `Radius * CenterMatchToleranceFraction` of the candidate circle center.
    private static bool CentroidMatches(JsonArray entitiesArr, Dictionary<long, int> index,
            List<long> edgeIds, double cx, double cy, double radius) {
        if (edgeIds.Count == 0) return false;
        double sumX = 0, sumY = 0;
        int count = 0;
        foreach (var id in edgeIds) {
            var edge = LookupEdgeNode(entitiesArr, index, id);
            var startX = edge?["start"]?["p"]?["x"]?.GetValue<double>();
            var startY = edge?["start"]?["p"]?["y"]?.GetValue<double>();
            if (startX is null || startY is null) continue;
            sumX += startX.Value;
            sumY += startY.Value;
            count++;
        }
        if (count == 0) return false;
        double polyCx = sumX / count;
        double polyCy = sumY / count;
        double tol = Math.Max(radius * CenterMatchToleranceFraction, Flags.GeometryTolerance);
        return Math.Abs(polyCx - cx) <= tol && Math.Abs(polyCy - cy) <= tol;
    }

    // Polygon perimeter (used to disambiguate inscribed vs circumscribed). Polygon edges
    // are equal length so any one edge × N suffices, but we sum to be robust against
    // sub-tolerance edge drift from solver settling.
    private static double ComputeRingPerimeter(JsonArray entitiesArr,
            Dictionary<long, int> index, List<long> edgeIds) {
        double total = 0;
        foreach (var id in edgeIds) {
            var edge = LookupEdgeNode(entitiesArr, index, id);
            var sx = edge?["start"]?["p"]?["x"]?.GetValue<double>() ?? 0.0;
            var sy = edge?["start"]?["p"]?["y"]?.GetValue<double>() ?? 0.0;
            var ex = edge?["end"]?["p"]?["x"]?.GetValue<double>() ?? 0.0;
            var ey = edge?["end"]?["p"]?["y"]?.GetValue<double>() ?? 0.0;
            var dx = ex - sx;
            var dy = ey - sy;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static JsonObject? LookupEdgeNode(JsonArray entitiesArr,
            Dictionary<long, int> index, long edgeId) {
        if (!index.TryGetValue(edgeId, out var idx)) return null;
        var node = entitiesArr[idx] as JsonObject;
        if (node?["kind"]?.GetValue<string>() != KindSketchLine) return null;
        return node;
    }

    // Constructs the SketchPolygon JSON entry. `edges` is filled with deep-cloned line
    // entity nodes (DeepClone so the originals can be removed from entitiesArr without
    // affecting the polygon's embedded copy); `circle` is a deep-clone of the
    // construction circle entity node. `first_vertex` is the first edge's END — a
    // polygon CORNER, read from the SAME slot SW's CreatePolygon writes its reference
    // point to, so source-capture and re-Add re-capture land on the identical corner
    // (see SketchPolygon.FirstVertexFromEdges) — which (with center + sides) fully
    // determines the polygon.
    private static JsonObject BuildPolygonNode(JsonArray entitiesArr,
            Dictionary<long, int> index, List<long> edgeIds, JsonObject circleNode,
            double centerX, double centerY, bool inscribed) {
        var edgesArr = new JsonArray();
        foreach (var id in edgeIds) {
            var edgeNode = LookupEdgeNode(entitiesArr, index, id);
            if (edgeNode is not null) {
                edgesArr.Add(edgeNode.DeepClone());
            }
        }
        // Determine `construction` from the first edge's flag — SW makes all edges
        // of a polygon share construction state.
        bool construction = false;
        if (edgesArr.FirstOrDefault() is JsonObject firstEdge) {
            construction = firstEdge["construction"]?.GetValue<bool>() ?? false;
        }
        var (fvX, fvY) = SketchPolygon.FirstVertexFromEdges(edgesArr, centerX, centerY);
        return new JsonObject {
            ["type"]         = KindSketchPolygon,
            ["center"]       = new JsonObject { ["x"] = centerX, ["y"] = centerY },
            ["first_vertex"] = new JsonObject { ["x"] = fvX, ["y"] = fvY },
            ["sides"]        = edgeIds.Count,
            ["inscribed"]    = inscribed,
            ["construction"] = construction,
            ["echo_edges"]   = edgesArr,
            ["echo_circle"]  = circleNode.DeepClone(),
        };
    }
}
