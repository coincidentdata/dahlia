using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Definitions;

// Shared region-boundary tessellation + containment helpers used by both
// `DefinitionCapture.Capture(SketchRegion)` (computes an interior witness
// point baked into `RegionDefinition.echo_interior_point`) and
// `File.ProbeRegion` (finds the region containing an author-supplied point).
//
// Sampling walks the region's loops via `Loop2.IGetFirstCoEdge() →
// CoEdge.IGetNext()` so we follow proper loop-traversal order with loop-
// trimmed parameter ranges. Each coedge gets tessellated via SW's own
// `ICurve.GetTessPts(chordTol, lenTol, startPt, endPt)` — 2 points for
// lines, N for arcs/splines.
//
// SW quirk: `ICoEdge.GetCurveParams()` returns a 10-element double[]:
//   [0..2] = start XYZ, [3..5] = end XYZ, [6] = startParam, [7] = endParam,
//   [8] sense (per docs: unused), [9] curve type (per docs: unused).
// Reading [0]/[1] as params (the textbook double[uMin, uMax] assumption)
// evaluates the curve at random U values — produces boundaries that wander
// outside the sketch's actual extent.
internal static class SketchRegionGeometry {
    private const double ChordTol = 1e-5;  // 10 μm — well below interior-point-to-boundary distance.

    internal static IReadOnlyList<IReadOnlyList<Point2D>> SampleLoops(
            SketchRegion region, Sketch sketch) {
        var modelToSketch = ((ISketch)sketch).ModelToSketchTransform
            ?? throw new InvalidOperationException(
                "SampleLoops: ModelToSketchTransform null on owning sketch");
        var axes = MathUtils.GetTransformMatrix(modelToSketch);

        var polylines = new List<IReadOnlyList<Point2D>>();
        var loop = region.GetFirstLoop();
        var loopSafety = 0;
        while (loop is not null) {
            loopSafety++;
            if (loopSafety > 32) {
                throw new InvalidOperationException(
                    "SampleLoops: loop iteration cap (32) hit");
            }
            var firstCoedge = loop.IGetFirstCoEdge();
            var coedge = firstCoedge;
            var coedgeSafety = 0;
            while (coedge is not null) {
                coedgeSafety++;
                if (coedgeSafety > 10000) {
                    throw new InvalidOperationException(
                        "SampleLoops: coedge walk exceeded 10000 (loop not terminating)");
                }
                if (coedge.GetCurveParams() is not double[] cp || cp.Length < 8) {
                    SldworksLog.Information(
                        "SampleLoops: coedge.GetCurveParams returned wrong shape (len={Len})",
                        (coedge.GetCurveParams() as Array)?.Length ?? -1);
                    coedge = NextCoedge(coedge, firstCoedge);
                    continue;
                }
                var startPt = new[] { cp[0], cp[1], cp[2] };
                var endPt = new[] { cp[3], cp[4], cp[5] };

                var coedgeCurve = coedge.IGetEdge()?.IGetCurve() as Curve;
                // SW quirk (2026): `ICurve.GetTessPts` returns null on LINE
                // curves (we observed every line-edge coedge in a sketch produce
                // len=-1). Fall back to the coedge's recorded start/end XYZ —
                // a line tessellates to exactly 2 points anyway, and those
                // points are already in `cp[0..5]`.
                double[] tess;
                if (coedgeCurve is null) {
                    tess = new[] {
                        cp[0], cp[1], cp[2],
                        cp[3], cp[4], cp[5],
                    };
                } else {
                    var swTess = coedgeCurve.GetTessPts(ChordTol, 0.0, startPt, endPt) as double[];
                    if (swTess is null || swTess.Length < 6) {
                        // Fallback: 2-point line tessellation from cp endpoints.
                        tess = new[] {
                            cp[0], cp[1], cp[2],
                            cp[3], cp[4], cp[5],
                        };
                    } else {
                        tess = swTess;
                    }
                }
                var samples = new List<Point2D>(tess.Length / 3);
                for (var i = 0; i + 2 < tess.Length; i += 3) {
                    var (lx, ly) = MathUtils.ProjectToSketchPlane(axes, tess[i], tess[i + 1], tess[i + 2]);
                    samples.Add(new Point2D(lx, ly));
                }
                if (samples.Count > 0) polylines.Add(samples);

                coedge = NextCoedge(coedge, firstCoedge);
            }
            loop = loop.IGetNext();
        }
        return polylines;
    }

    // Order-invariant PNPoly: count +x ray crossings per polyline, sum mod 2.
    // Works even when polylines aren't given in adjacency order (e.g. a
    // region's outer + inner-hole loops are independent).
    internal static bool PointInRegion(
            IReadOnlyList<IReadOnlyList<Point2D>> polylines, double testX, double testY) {
        var crossings = 0;
        foreach (var poly in polylines) {
            for (var i = 1; i < poly.Count; i++) {
                var a = poly[i - 1];
                var b = poly[i];
                if ((a.Y > testY) != (b.Y > testY)) {
                    var crossX = a.X + (testY - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                    if (testX < crossX) crossings++;
                }
            }
        }
        return (crossings & 1) == 1;
    }

    internal static double LoopsBboxArea(IReadOnlyList<IReadOnlyList<Point2D>> polylines) {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var poly in polylines) {
            foreach (var p in poly) {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
        return (maxX - minX) * (maxY - minY);
    }

    // Pick an interior witness point for a region — must be DEEP inside, not
    // on the boundary, so a downstream `ProbeRegion(point)` call against a
    // freshly-rebuilt sketch (which may have a different region decomposition
    // than source's) lands on the same region. Boundary points are
    // ambiguous: PNPoly is sensitive to float noise on boundary samples,
    // and overlapping regions sharing a boundary both contain a boundary
    // point.
    //
    // Strategy: bbox center (clearly inside for convex regions), then spiral
    // search if the center sits outside (concave / donut / L-shape). The
    // bbox center beats boundary-sample centroid: for a curved region like
    // a circle-arc lens, the samples bunch along the arc, dragging the
    // centroid onto the arc boundary itself.
    internal static Point2D ComputeInteriorPoint(IReadOnlyList<IReadOnlyList<Point2D>> polylines) {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var totalPts = 0;
        foreach (var poly in polylines) {
            foreach (var p in poly) {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                totalPts++;
            }
        }
        if (totalPts == 0) {
            throw new InvalidOperationException(
                "ComputeInteriorPoint: region has no boundary samples");
        }
        var cx = (minX + maxX) / 2.0;
        var cy = (minY + maxY) / 2.0;
        if (PointInRegion(polylines, cx, cy)) return new Point2D(cx, cy);

        // Bbox center sits outside (concave / donut / L-shape). Grid-scan the
        // bbox dense enough to find any non-trivial interior. Each new attempt
        // perturbs by a golden-ratio offset to dodge axes of symmetry.
        var dx = maxX - minX;
        var dy = maxY - minY;
        const int gridN = 16;  // 16x16 = 256 candidates inside the bbox itself
        for (var iy = 1; iy < gridN; iy++) {
            for (var ix = 1; ix < gridN; ix++) {
                var px = minX + dx * ix / gridN;
                var py = minY + dy * iy / gridN;
                if (PointInRegion(polylines, px, py)) return new Point2D(px, py);
            }
        }
        // Last-resort log dump so the next failure is diagnosable. Polylines
        // are emitted as their first/last/count summary — full enumeration
        // would flood the log.
        for (var pi = 0; pi < polylines.Count; pi++) {
            var poly = polylines[pi];
            SldworksLog.Information(
                "ComputeInteriorPoint: polyline[{Idx}] n={N} " +
                "first=({Fx},{Fy}) last=({Lx},{Ly})",
                pi, poly.Count,
                poly.Count > 0 ? poly[0].X : double.NaN,
                poly.Count > 0 ? poly[0].Y : double.NaN,
                poly.Count > 0 ? poly[poly.Count - 1].X : double.NaN,
                poly.Count > 0 ? poly[poly.Count - 1].Y : double.NaN);
        }
        SldworksLog.Information(
            "ComputeInteriorPoint: bbox=({Lx},{Ly})-({Hx},{Hy}) center=({Cx},{Cy}) " +
            "polylines.Count={Pc} totalPts={N}",
            minX, minY, maxX, maxY, cx, cy, polylines.Count, totalPts);
        throw new InvalidOperationException(
            $"ComputeInteriorPoint: failed to find an interior witness over " +
            $"{gridN * gridN} bbox-grid candidates — " +
            "region likely has zero area, self-intersecting boundary, or PNPoly " +
            "is rejecting all candidates (see log dump above)");
    }

    private static CoEdge? NextCoedge(CoEdge current, CoEdge first) {
        var next = current.IGetNext();
        return (next is null || ReferenceEquals(next, first)) ? null : next;
    }
}
