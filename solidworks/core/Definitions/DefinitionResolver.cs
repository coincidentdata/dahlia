using System.Text.Json.Nodes;
using Sldworks.Core.Handlers.Sketches.Entities;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using PointEntity = Sldworks.Core.Handlers.Sketches.Entities.Point;

namespace Sldworks.Core.Definitions;

// Resolves Definitions to live COM entities by walking the model and matching geometry within Flags.GeometryTolerance. Multi-match throws.
internal static class DefinitionResolver {
    private static readonly double Tol = Flags.GeometryTolerance;

    // Sketch region edges round-trip with float noise from sketch-plane projection
    // (e.g. captured z = 6.28e-7 vs target z = 0 for the same 2D region). The default
    // 1e-7 tolerance is too tight for that noise; loosen by an order of magnitude for
    // region-level matching only. Solid-body / face / non-sketch edge matching keeps
    // the strict tolerance.
    private const double SketchRegionEdgeTol = 1e-6;

    internal static object? Resolve(File file, Definition def, Sketch? activeSketchHint = null) {
        var modelDoc = file.ModelDoc;
        return def switch {
            ComponentDefinition d       => file.ResolveComponent(d.Path),
            ComponentEntityDefinition d => file.ResolveComponentEntity(d),
            PlanarFaceDefinition d       => ResolveFace(file, d),
            CylindricalFaceDefinition d  => ResolveFace(file, d),
            ConicalFaceDefinition d      => ResolveFace(file, d),
            SphericalFaceDefinition d    => ResolveFace(file, d),
            ToroidalFaceDefinition d     => ResolveFace(file, d),
            BSurfaceFaceDefinition d     => ResolveFace(file, d),
            LineEdgeDefinition d         => ResolveEdge(file, d),
            CircularEdgeDefinition d     => ResolveEdge(file, d),
            EllipticalEdgeDefinition d   => ResolveEdge(file, d),
            SplineEdgeDefinition d       => ResolveEdge(file, d),
            VertexDefinition d           => ResolveVertex(file, d),
            BodyDefinition d             => ResolveBody(file, d),
            // Both bare ids and wrapped flavors route through the same id-based lookup;
            // SketchEntityId.EntityKind tells the resolver which COM subtype to walk so
            // (sketch_name, entity_kind, id) names exactly one live entity.
            SketchEntityId d             => ResolveSketchEntityId(file, d, activeSketchHint),
            SketchEntityDefinition d     => ResolveSketchEntityId(file, d.Id, activeSketchHint),
            SketchContourDefinition d    => ResolveSketchContour(file, d),
            RegionDefinition d           => ResolveRegion(file, d),
            RefAxisDefinition d          => ResolveRefAxis(modelDoc, d),
            TempAxisDefinition d         => ResolveTempAxis(file, d),
            RefPlaneDefinition d         => ResolveRefPlane(file, d),
            FeatureDefinition d          => GetFeatureByName(modelDoc, d.Name),
            _                            => null,
        };
    }

    // -- Face resolution ----------------------------------------------------

    private static Face2? ResolveFace(File file, Definition def) {
        // Parent body is a deterministic anchor — narrow the walk to one body when
        // present so faces on other bodies with identical analytic params can't collide.
        var parentBody = GetFaceParentBody(def);
        var matches = new List<Face2>();
        foreach (var body in EnumerateBodies(file)) {
            if (parentBody is not null && !BodyMatches(body, parentBody)) continue;
            var faces = (object[]?)body.GetFaces();
            if (faces is null) continue;
            foreach (Face2 face in faces) {
                if (FaceMatches(face, def)) matches.Add(face);
            }
        }
        // Diagnostic: when we resolved nothing, dump every body's mass-prop deltas
        // and (for those that passed the body filter) every face's analytic deltas.
        // Information-level so it lands in sldworks.log without flipping global
        // verbosity. Fires only on no-match so the hot path is untouched.
        if (matches.Count == 0) {
            DiagnoseFaceResolution(file, def, parentBody);
        }
        return PickSingle(file, matches, $"face/{def.GetType().Name}");
    }

    private static void DiagnoseFaceResolution(File file, Definition def, BodyDefinition? parentBody) {
        SldworksLog.Information(
            "ResolveFace[{Kind}] NO MATCH; payload={Payload}",
            def.GetType().Name, def.ToJson().ToJsonString());

        var bodyIdx = -1;
        foreach (var body in EnumerateBodies(file)) {
            bodyIdx++;
            var (centroid, volume, sa) = MassProperties.Compute(body);
            var bbox = BodyBbox(body);
            bool bodyPass = parentBody is null;
            if (parentBody is not null) {
                var centroidDelta = MaxAbsDelta(centroid, parentBody.Centroid);
                var volumeDelta = Math.Abs(volume - parentBody.Volume);
                var saDelta = Math.Abs(sa - parentBody.SurfaceArea);
                var bboxMinDelta = MaxAbsDelta(bbox.Min, parentBody.Bbox.Min);
                var bboxMaxDelta = MaxAbsDelta(bbox.Max, parentBody.Bbox.Max);
                bodyPass = centroidDelta <= Flags.BodyCentroidTol
                        && volumeDelta <= Flags.BodyVolumeTol
                        && saDelta <= Flags.BodyAreaTol
                        && bboxMinDelta <= Tol
                        && bboxMaxDelta <= Tol;
                SldworksLog.Information(
                    "  body[{Bi}] centroid=({X:G6},{Y:G6},{Z:G6}) vol={V:G6} sa={S:G6}: " +
                    "centroidDelta={Cd:G3} (massTol={Mt:G3}) volDelta={Vd:G3} saDelta={Sd:G3} " +
                    "bboxMinDelta={BMin:G3} bboxMaxDelta={BMax:G3} FILTER_PASS={Pass}",
                    bodyIdx, centroid.X, centroid.Y, centroid.Z, volume, sa,
                    centroidDelta, Flags.BodyMassPropertyTol, volumeDelta, saDelta,
                    bboxMinDelta, bboxMaxDelta, bodyPass);
                SldworksLog.Information(
                    "    bbox.min: live=({Lx:G8},{Ly:G8},{Lz:G8}) src=({Sx:G8},{Sy:G8},{Sz:G8}) delta=({Dx:G3},{Dy:G3},{Dz:G3})",
                    bbox.Min.X, bbox.Min.Y, bbox.Min.Z,
                    parentBody.Bbox.Min.X, parentBody.Bbox.Min.Y, parentBody.Bbox.Min.Z,
                    bbox.Min.X - parentBody.Bbox.Min.X, bbox.Min.Y - parentBody.Bbox.Min.Y, bbox.Min.Z - parentBody.Bbox.Min.Z);
                SldworksLog.Information(
                    "    bbox.max: live=({Lx:G8},{Ly:G8},{Lz:G8}) src=({Sx:G8},{Sy:G8},{Sz:G8}) delta=({Dx:G3},{Dy:G3},{Dz:G3})",
                    bbox.Max.X, bbox.Max.Y, bbox.Max.Z,
                    parentBody.Bbox.Max.X, parentBody.Bbox.Max.Y, parentBody.Bbox.Max.Z,
                    bbox.Max.X - parentBody.Bbox.Max.X, bbox.Max.Y - parentBody.Bbox.Max.Y, bbox.Max.Z - parentBody.Bbox.Max.Z);
            }
            if (!bodyPass) continue;
            DiagnoseFacesOnBody(body, def, bodyIdx);
        }
    }

    private static void DiagnoseFacesOnBody(Body2 body, Definition def, int bodyIdx) {
        var faces = (object[]?)body.GetFaces();
        if (faces is null) {
            SldworksLog.Information("  body[{Bi}]: GetFaces returned null", bodyIdx);
            return;
        }
        int faceIdx = -1, inspected = 0;
        foreach (Face2 face in faces) {
            faceIdx++;
            var surface = (Surface?)face.IGetSurface();
            if (surface is null) continue;
            // Only log faces of the same flavor as the target — keep noise down.
            if (def is PlanarFaceDefinition pfd) {
                if (!surface.IsPlane()) continue;
                inspected++;
                var fn = FaceNormal(face);
                var closest = (double[])face.GetClosestPointOn(pfd.OnFacePoint.X, pfd.OnFacePoint.Y, pfd.OnFacePoint.Z);
                var closestP = new Point3D(closest[0], closest[1], closest[2]);
                var cd = MaxAbsDelta(closestP, pfd.OnFacePoint);
                var fnLen = Math.Sqrt(fn.X * fn.X + fn.Y * fn.Y + fn.Z * fn.Z);
                var dot = fnLen <= Tol ? 0.0
                    : (fn.X * pfd.Normal.X + fn.Y * pfd.Normal.Y + fn.Z * pfd.Normal.Z) / fnLen;
                var onFaceOk = cd <= Tol;
                var normalOk = Math.Abs(Math.Abs(dot) - 1.0) <= Tol;
                SldworksLog.Information(
                    "    body[{Bi}].face[{Fi}] planar: closest=({X:G6},{Y:G6},{Z:G6}) normal=({NX:G3},{NY:G3},{NZ:G3}) " +
                    "onFaceDelta={Cd:G3} (geoTol={Gt:G3}, ok={Cok}) |normal·expected|={Dot:G6} (parallel_ok={Nok})",
                    bodyIdx, faceIdx, closestP.X, closestP.Y, closestP.Z, fn.X, fn.Y, fn.Z,
                    cd, Tol, onFaceOk, Math.Abs(dot), normalOk);
            }
        }
        SldworksLog.Information(
            "  body[{Bi}]: {N} face(s) of target flavor inspected, none matched", bodyIdx, inspected);
    }

    private static double MaxAbsDelta(Point3D a, Point3D b) =>
        Math.Max(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)), Math.Abs(a.Z - b.Z));

    // ParentBody is typed as `Definition?` on each face record so the abstract-typed
    // JsonConverter fires and emits a `kind` tag (concrete-typed properties skip the
    // converter). Every captured parent is a BodyDefinition; cast through.
    private static BodyDefinition? GetFaceParentBody(Definition def) => (def switch {
        PlanarFaceDefinition d      => d.ParentBody,
        CylindricalFaceDefinition d => d.ParentBody,
        ConicalFaceDefinition d     => d.ParentBody,
        SphericalFaceDefinition d   => d.ParentBody,
        ToroidalFaceDefinition d    => d.ParentBody,
        BSurfaceFaceDefinition d    => d.ParentBody,
        _                           => null,
    }) as BodyDefinition;

    private static bool FaceMatches(Face2 face, Definition def) {
        var surface = (Surface?)face.IGetSurface();
        if (surface is null) return false;

        switch (def) {
            case PlanarFaceDefinition d:
                if (!surface.IsPlane()) return false;
                if (!DirectionsParallel(FaceNormal(face), d.Normal)) return false;
                return OnFaceMatches(face, d.OnFacePoint);

            case CylindricalFaceDefinition d:
                if (!surface.IsCylinder()) return false;
                // SW quirk: CylinderParams = [origin xyz, axis xyz, radius].
                var cyl = (double[])surface.CylinderParams;
                var cylAxis = new Direction(cyl[3], cyl[4], cyl[5]);
                if (Math.Abs(cyl[6] - d.Radius) > Tol) return false;
                if (!DirectionsParallel(cylAxis, d.AxisDirection)) return false;
                // Cylinder origin isn't unique — compare line-distance, not point-equality.
                if (!PointOnAxis(d.AxisOrigin, new Point3D(cyl[0], cyl[1], cyl[2]), cylAxis)) return false;
                return OnFaceMatches(face, d.OnFacePoint);

            case ConicalFaceDefinition d:
                if (!surface.IsCone()) return false;
                // SW quirk: ConeParams2 = [apex xyz, axis xyz, radius_at_offset, half_angle].
                // Index 6 is a radius reference, NOT the half-angle — half-angle is index 7.
                var cone = (double[])surface.ConeParams2;
                if (!PointWithinTol(new Point3D(cone[0], cone[1], cone[2]), d.Apex)) return false;
                if (!DirectionsParallel(new Direction(cone[3], cone[4], cone[5]), d.AxisDirection)) return false;
                if (Math.Abs(cone[7] - d.HalfAngle) > Tol) return false;
                return OnFaceMatches(face, d.OnFacePoint);

            case SphericalFaceDefinition d:
                if (!surface.IsSphere()) return false;
                var sph = (double[])surface.SphereParams;
                if (!PointWithinTol(new Point3D(sph[0], sph[1], sph[2]), d.Center)) return false;
                if (Math.Abs(sph[3] - d.Radius) > Tol) return false;
                return OnFaceMatches(face, d.OnFacePoint);

            case ToroidalFaceDefinition d:
                if (!surface.IsTorus()) return false;
                // Torus center is unique (unlike cylinder origin), so PointWithinTol works.
                var tor = (double[])surface.TorusParams;
                if (!PointWithinTol(new Point3D(tor[0], tor[1], tor[2]), d.Center)) return false;
                if (!DirectionsParallel(new Direction(tor[3], tor[4], tor[5]), d.AxisDirection)) return false;
                if (Math.Abs(tor[6] - d.MajorRadius) > Tol) return false;
                if (Math.Abs(tor[7] - d.MinorRadius) > Tol) return false;
                return OnFaceMatches(face, d.OnFacePoint);

            case BSurfaceFaceDefinition d:
                // Skip analytic surfaces — they match a flavor above.
                if (surface.IsPlane() || surface.IsCylinder() || surface.IsCone()
                    || surface.IsSphere() || surface.IsTorus()) {
                    return false;
                }
                if (d.OnFacePoints.Count == 0) return false;
                foreach (var sample in d.OnFacePoints) {
                    if (!OnFaceMatches(face, sample)) return false;
                }
                return true;

            default:
                return false;
        }
    }

    // Universal trim-disambiguator: the captured OnFacePoint lies on the source
    // face's trimmed extent by construction (CaptureOnFacePoint snaps via
    // GetClosestPointOn). On rebuild, if this candidate face is the same physical
    // face, GetClosestPointOn returns the same point (distance ≈ 0). If candidate
    // is a different face sharing analytic params (e.g. stacked coaxial cylinder,
    // adjacent coplanar sub-face), GetClosestPointOn returns a point on ITS trim
    // — distance > Tol. Rotation-invariant under SW's in-plane UV reparameterization
    // because we never read UV; SW computes the projection internally.
    private static bool OnFaceMatches(Face2 face, Point3D point) {
        var closest = (double[])face.GetClosestPointOn(point.X, point.Y, point.Z);
        return PointsWithin(new Point3D(closest[0], closest[1], closest[2]), point, Tol);
    }

    // -- Edge resolution ----------------------------------------------------

    private static Edge? ResolveEdge(File file, Definition def) {
        var parentBody = GetEdgeParentBody(def);
        var matches = new List<Edge>();
        var bodiesSeen = 0;
        var bodyMatchedCount = 0;
        var edgesSeen = 0;
        foreach (var body in EnumerateBodies(file)) {
            bodiesSeen++;
            if (parentBody is not null && !BodyMatches(body, parentBody)) continue;
            bodyMatchedCount++;
            var edges = (object[]?)body.GetEdges();
            if (edges is null) continue;
            foreach (Edge edge in edges) {
                edgesSeen++;
                if (EdgeMatches(edge, def)) matches.Add(edge);
            }
        }
        if (matches.Count == 0) {
            SldworksLog.Information(
                "ResolveEdge[{Kind}] NO MATCH (bodiesSeen={Bs}, bodyMatched={Bm}, edgesSeenInMatched={Es}); payload={Payload}",
                def.GetType().Name, bodiesSeen, bodyMatchedCount, edgesSeen, def.ToJson().ToJsonString());
        }
        return PickSingle(file, matches, $"edge/{def.GetType().Name}");
    }

    private static BodyDefinition? GetEdgeParentBody(Definition def) => (def switch {
        LineEdgeDefinition d       => d.ParentBody,
        CircularEdgeDefinition d   => d.ParentBody,
        EllipticalEdgeDefinition d => d.ParentBody,
        SplineEdgeDefinition d     => d.ParentBody,
        _                          => null,
    }) as BodyDefinition;

    private static bool EdgeMatches(Edge edge, Definition def) => EdgeMatches(edge, def, Tol);

    private static bool EdgeMatches(Edge edge, Definition def, double tol) {
        var curve = (Curve?)edge.IGetCurve();
        if (curve is null) return false;

        switch (def) {
            case LineEdgeDefinition d:
                if (!curve.IsLine()) return false;
                var startV = edge.IGetStartVertex();
                var endV = edge.IGetEndVertex();
                if (startV is null || endV is null) return false;
                var s = ToPoint3D((double[])startV.GetPoint());
                var e = ToPoint3D((double[])endV.GetPoint());
                // Line edge is undirected — accept either orientation.
                return (PointsWithin(s, d.Start, tol) && PointsWithin(e, d.End, tol))
                    || (PointsWithin(s, d.End, tol)   && PointsWithin(e, d.Start, tol));

            case CircularEdgeDefinition d:
                if (!curve.IsCircle()) return false;
                var circ = (double[])curve.CircleParams;
                var ccent = new Point3D(circ[0], circ[1], circ[2]);
                var caxis = new Direction(circ[3], circ[4], circ[5]);
                if (!PointsWithin(ccent, d.Center, tol)) return false;
                if (Math.Abs(circ[6] - d.Radius) > tol) return false;
                if (!DirectionsParallel(caxis, d.AxisDirection)) return false;
                // SW quirk: closed-curve edges have null start/end vertices. Captured
                // full circle (null) must match a closed live edge; arc (non-null) must
                // match an open edge with matching endpoints, either orientation.
                var circStartV = edge.IGetStartVertex();
                var circEndV = edge.IGetEndVertex();
                if (d.Start is null || d.End is null) {
                    return circStartV is null && circEndV is null;
                }
                if (circStartV is null || circEndV is null) return false;
                var cs = ToPoint3D((double[])circStartV.GetPoint());
                var ce = ToPoint3D((double[])circEndV.GetPoint());
                return (PointsWithin(cs, d.Start, tol) && PointsWithin(ce, d.End, tol))
                    || (PointsWithin(cs, d.End, tol)   && PointsWithin(ce, d.Start, tol));

            case EllipticalEdgeDefinition d:
                if (!curve.IsEllipse()) return false;
                // SW quirk: GetEllipseParams = [center xyz, major_radius, major_axis xyz,
                // minor_radius, minor_axis xyz]. Both axes flip independently across rebuilds.
                var ell = (double[])curve.GetEllipseParams();
                if (!PointWithinTol(new Point3D(ell[0], ell[1], ell[2]), d.Center)) return false;
                if (Math.Abs(ell[3] - d.MajorRadius) > Tol) return false;
                if (Math.Abs(ell[7] - d.MinorRadius) > Tol) return false;
                if (!DirectionsParallel(new Direction(ell[4], ell[5], ell[6]), d.MajorAxis)) return false;
                if (!DirectionsParallel(new Direction(ell[8], ell[9], ell[10]), d.MinorAxis)) return false;
                // SW quirk: closed-curve edges have null start/end vertices. Captured
                // full ellipse (null) must match a closed live edge; arc (non-null) must
                // match an open edge with matching endpoints, either orientation.
                var ellStartV = edge.IGetStartVertex();
                var ellEndV = edge.IGetEndVertex();
                if (d.Start is null || d.End is null) {
                    return ellStartV is null && ellEndV is null;
                }
                if (ellStartV is null || ellEndV is null) return false;
                var es = ToPoint3D((double[])ellStartV.GetPoint());
                var ee = ToPoint3D((double[])ellEndV.GetPoint());
                return (PointWithinTol(es, d.Start) && PointWithinTol(ee, d.End))
                    || (PointWithinTol(es, d.End)   && PointWithinTol(ee, d.Start));

            case SplineEdgeDefinition d:
                // SW quirk: IsBcurve() is true for genuine splines AND helix edges; helix
                // edges may collide with this flavor (multi-match throws at PickSingle).
                if (!curve.IsBcurve()) return false;
                return SplineMatches(curve, d);

            default:
                return false;
        }
    }

    private static bool SplineMatches(Curve curve, SplineEdgeDefinition d) {
        // Sample at evenly-spaced parameter values; more robust than diffing control points/knots.
        if (d.ControlPoints.Count == 0) return false;
        curve.GetEndParams(out var startU, out var endU, out _, out _);
        var n = d.ControlPoints.Count - 1;
        var samples = new Point3D[n + 1];
        for (var i = 0; i <= n; i++) {
            var t = n == 0 ? startU : startU + (endU - startU) * i / n;
            var pt = (double[])curve.Evaluate2(t, 0);
            samples[i] = ToPoint3D(pt);
        }
        var forward = true;
        for (var i = 0; i <= n; i++) {
            if (!PointWithinTol(samples[i], d.ControlPoints[i])) { forward = false; break; }
        }
        if (forward) return true;
        // SW quirk: closed splines can flip parameter direction across rebuilds.
        for (var i = 0; i <= n; i++) {
            if (!PointWithinTol(samples[i], d.ControlPoints[n - i])) return false;
        }
        return true;
    }

    // -- Vertex / body / sketch entity --------------------------------------

    private static Vertex? ResolveVertex(File file, VertexDefinition def) {
        var matches = new List<Vertex>();
        foreach (var body in EnumerateBodies(file)) {
            if (def.ParentBody is BodyDefinition pb && !BodyMatches(body, pb)) continue;
            var verts = (object[]?)body.GetVertices();
            if (verts is null) continue;
            foreach (Vertex v in verts) {
                var p = ToPoint3D((double[])v.GetPoint());
                if (PointWithinTol(p, def.Point)) matches.Add(v);
            }
        }
        return PickSingle(file, matches, "vertex");
    }

    private static Body2? ResolveBody(File file, BodyDefinition def) {
        var matches = new List<Body2>();
        foreach (var body in EnumerateBodies(file)) {
            if (BodyMatches(body, def)) matches.Add(body);
        }
        // Diagnostic: on no-match, dump every live body's fingerprint deltas
        // vs the expected so we can see exactly which criterion failed and by
        // how much (mass props at BodyMassPropertyTol, bbox at GeometryTolerance).
        // Information-level so it lands in sldworks.log without flipping global
        // verbosity. Fires only on no-match so the hot path is untouched.
        if (matches.Count == 0) {
            DiagnoseBodyResolution(file, def);
        }
        return PickSingle(file, matches, "body");
    }

    private static void DiagnoseBodyResolution(File file, BodyDefinition def) {
        SldworksLog.Information(
            "ResolveBody NO MATCH; expected centroid=({X:G8},{Y:G8},{Z:G8}) vol={V:G8} sa={S:G8} " +
            "bbox.min=({MnX:G8},{MnY:G8},{MnZ:G8}) bbox.max=({MxX:G8},{MxY:G8},{MxZ:G8})",
            def.Centroid.X, def.Centroid.Y, def.Centroid.Z, def.Volume, def.SurfaceArea,
            def.Bbox.Min.X, def.Bbox.Min.Y, def.Bbox.Min.Z,
            def.Bbox.Max.X, def.Bbox.Max.Y, def.Bbox.Max.Z);
        var bodyIdx = -1;
        foreach (var body in EnumerateBodies(file)) {
            bodyIdx++;
            var (centroid, volume, sa) = MassProperties.Compute(body);
            var bbox = BodyBbox(body);
            var centroidDelta = MaxAbsDelta(centroid, def.Centroid);
            var volumeDelta = Math.Abs(volume - def.Volume);
            var saDelta = Math.Abs(sa - def.SurfaceArea);
            var bboxMinDelta = MaxAbsDelta(bbox.Min, def.Bbox.Min);
            var bboxMaxDelta = MaxAbsDelta(bbox.Max, def.Bbox.Max);
            SldworksLog.Information(
                "  body[{Bi}] centroid=({X:G8},{Y:G8},{Z:G8}) vol={V:G8} sa={S:G8}: " +
                "centroidDelta={Cd:G3} volDelta={Vd:G3} saDelta={Sd:G3} " +
                "bboxMinDelta={BMin:G3} bboxMaxDelta={BMax:G3} " +
                "(massTol={Mt:G3}, geoTol={Gt:G3})",
                bodyIdx, centroid.X, centroid.Y, centroid.Z, volume, sa,
                centroidDelta, volumeDelta, saDelta, bboxMinDelta, bboxMaxDelta,
                Flags.BodyMassPropertyTol, Tol);
            SldworksLog.Information(
                "    bbox.min: live=({Lx:G8},{Ly:G8},{Lz:G8}) src=({Sx:G8},{Sy:G8},{Sz:G8}) delta=({Dx:G3},{Dy:G3},{Dz:G3})",
                bbox.Min.X, bbox.Min.Y, bbox.Min.Z,
                def.Bbox.Min.X, def.Bbox.Min.Y, def.Bbox.Min.Z,
                bbox.Min.X - def.Bbox.Min.X, bbox.Min.Y - def.Bbox.Min.Y, bbox.Min.Z - def.Bbox.Min.Z);
            SldworksLog.Information(
                "    bbox.max: live=({Lx:G8},{Ly:G8},{Lz:G8}) src=({Sx:G8},{Sy:G8},{Sz:G8}) delta=({Dx:G3},{Dy:G3},{Dz:G3})",
                bbox.Max.X, bbox.Max.Y, bbox.Max.Z,
                def.Bbox.Max.X, def.Bbox.Max.Y, def.Bbox.Max.Z,
                bbox.Max.X - def.Bbox.Max.X, bbox.Max.Y - def.Bbox.Max.Y, bbox.Max.Z - def.Bbox.Max.Z);
        }
        if (bodyIdx < 0) {
            SldworksLog.Information("  (no live bodies present in document)");
        }
    }

    private static bool BodyMatches(Body2 body, BodyDefinition def) {
        // Geometric identity — mass props at their per-unit tolerances
        // (centroid 1e-4 m, volume 5e-8 m^3, area 2e-5 m^2 — see Flags), bbox
        // at the tight GeometryTolerance (1e-7) on all 6 axis components with
        // a single-outlier exemption: ONE of the 6 (min.{x,y,z}, max.{x,y,z})
        // is allowed to exceed GeometryTolerance up to BodyMassPropertyTol.
        // Loft / sweep surfaces fit with anisotropic NURBS noise — one axis
        // of the bounding box can drift by single µm on rebuild even when the
        // body is otherwise the same. Letting that one axis float keeps the
        // matcher honest (5 tight components is still a strong fingerprint)
        // without rejecting bodies that match on mass props.
        var (centroid, volume, surfaceArea) = MassProperties.Compute(body);
        if (!PointsWithin(centroid, def.Centroid, Flags.BodyCentroidTol)) return false;
        if (Math.Abs(volume - def.Volume) > Flags.BodyVolumeTol) return false;
        if (Math.Abs(surfaceArea - def.SurfaceArea) > Flags.BodyAreaTol) return false;
        var bbox = BodyBbox(body);
        return BboxMatchesWithOneOutlier(bbox, def.Bbox);
    }

    private static bool BboxMatchesWithOneOutlier(BoundingBox live, BoundingBox src) {
        Span<double> deltas = stackalloc double[6] {
            Math.Abs(live.Min.X - src.Min.X),
            Math.Abs(live.Min.Y - src.Min.Y),
            Math.Abs(live.Min.Z - src.Min.Z),
            Math.Abs(live.Max.X - src.Max.X),
            Math.Abs(live.Max.Y - src.Max.Y),
            Math.Abs(live.Max.Z - src.Max.Z),
        };
        var outliers = 0;
        foreach (var d in deltas) {
            if (d <= Tol) continue;
            if (d > Flags.BodyMassPropertyTol) return false;  // even the slack tol busted
            if (++outliers > 1) return false;                  // more than one axis off
        }
        return true;
    }

    // Resolves a SketchEntityId to the live SW COM entity. SW quirk: SW's int[2]
    // GetID() is unique only within ONE COM subtype's namespace — a SketchLine, a
    // SketchArc, a SketchEllipse, a SketchPoint, etc. can all report the same pair in
    // the same sketch. id.EntityKind selects which namespace to walk and which COM
    // subtype to keep, so the (SketchName, EntityKind, Id) triplet is unambiguous.
    private static object? ResolveSketchEntityId(File file, SketchEntityId id, Sketch? activeSketchHint) {
        var sketch = FindSketchByFeatureName(file, id.SketchName)
            ?? activeSketchHint
            ?? throw new InvalidOperationException(
                $"ResolveSketchEntityId: no sketch feature named '{id.SketchName}' in the active " +
                "model document, and no active-sketch hint");

        if (id.EntityKind == SketchEntityId.KindPoint) {
            // SW quirk: GetSketchPoints2() returns only user points; constraint refs can
            // target SW-internal points (curve endpoints, parabola apex/focal/start/end,
            // ellipse axis points, etc.). Use GetSketchPoints() for the full set.
            // SW quirk: MergePoints can re-id endpoints in target's sketch differently
            // from source — the Sketch Add path builds a source→live map from each
            // segment's child Point definitions and checks it before falling here.
            if (sketch.GetSketchPointsCount() > 0 && (object[]?)sketch.GetSketchPoints() is { } points) {
                foreach (SketchPoint pt in points) {
                    if (CombinedIdMatches((pt as ISketchPoint)?.GetID() as int[], id.Id)) {
                        return pt;
                    }
                }
            }
            throw new InvalidOperationException(
                $"ResolveSketchEntityId: no SketchPoint with id {id.Id} in sketch '{id.SketchName}'");
        }

        Predicate<SketchSegment> matchesKind = SegmentKindPredicate(id.EntityKind);

        var segments = (object[]?)sketch.GetSketchSegments();
        if (segments is not null) {
            foreach (SketchSegment seg in segments) {
                if (matchesKind(seg) && CombinedIdMatches(seg.GetID() as int[], id.Id)) {
                    return seg;
                }
            }
        }
        throw new InvalidOperationException(
            $"ResolveSketchEntityId: no {id.EntityKind} SketchSegment with id {id.Id} " +
            $"in sketch '{id.SketchName}'");
    }

    private static Sketch? FindSketchByFeatureName(File file, string sketchName) {
        var feat = file.ModelDoc.IFirstFeature();
        while (feat != null) {
            if (string.Equals(feat.Name, sketchName, StringComparison.Ordinal)
                && feat.GetSpecificFeature2() is Sketch sketch) {
                return sketch;
            }
            feat = feat.IGetNextFeature();
        }
        return null;
    }

    private static bool CombinedIdMatches(int[]? ids, long combined) =>
        SketchEntityId.CombineFrom(ids) == combined;

    private static Predicate<SketchSegment> SegmentKindPredicate(string entityKind) => entityKind switch {
        SketchEntityId.KindLine     => seg => seg is SketchLine,
        SketchEntityId.KindArc      => seg => seg is SketchArc,
        SketchEntityId.KindEllipse  => seg => seg is SketchEllipse,
        SketchEntityId.KindParabola => seg => seg is SketchParabola,
        SketchEntityId.KindSpline   => seg => seg is SketchSpline,
        _ => throw new InvalidOperationException(
            $"SegmentKindPredicate: '{entityKind}' is not a SketchSegment EntityKind " +
            "(expected one of line | arc | ellipse | parabola | spline)"),
    };

    private static SketchSegment? FindSketchSegmentInSketch(Sketch sketch, SketchEntityId id) {
        var segments = (object[]?)sketch.GetSketchSegments();
        if (segments is null) return null;
        var matchesKind = SegmentKindPredicate(id.EntityKind);
        foreach (SketchSegment seg in segments) {
            if (matchesKind(seg) && CombinedIdMatches(seg.GetID() as int[], id.Id)) return seg;
        }
        return null;
    }

    private static object ResolveSketchContour(File file, SketchContourDefinition def)
        => ResolveSketchContourInSketch(file, def, null);

    // SW quirk: contour / region decomposition is not stable across rebuilds — the same segments can repartition into a different count, so match by segment-/edge-subset, not contour identity.
    internal static List<object> ResolveContoursForSelection(
        File file, IReadOnlyList<Definition> contours, Sketch? scopeSketch) {
        var results = new List<object>(contours.Count);
        if (contours.Count == 0) return results;

        // Union source segment-ids (from SketchContourDefinitions, resolved against the live
        // target so reissued ids round-trip) with source edge definitions (from RegionDefinitions,
        // kept as Definitions so subset matching uses tolerance-based EdgeMatches).
        // Combined-long ids match the wire SketchEntityId.Id shape.
        var sourceSegIds = new HashSet<long>();
        var sourceEdgeDefs = new List<Definition>();
        Sketch? owningSketch = scopeSketch;
        var others = new List<Definition>();

        foreach (var contour in contours) {
            switch (contour) {
                case SketchContourDefinition scd:
                    if (scd.Segments is null || scd.Segments.Count == 0) continue;
                    foreach (var segDef in scd.Segments) {
                        var hostSketch = scopeSketch ?? FindSketchByFeatureName(file, segDef.Id.SketchName);
                        if (hostSketch is null) continue;
                        var seg = FindSketchSegmentInSketch(hostSketch, segDef.Id);
                        if (seg is null) continue;
                        var combined = SketchEntityId.CombineFrom(seg.GetID() as int[]);
                        if (combined is null) continue;
                        sourceSegIds.Add(combined.Value);
                        if (owningSketch is null) owningSketch = hostSketch;
                    }
                    break;
                case RegionDefinition rd:
                    foreach (var edgeDef in rd.Edges) {
                        sourceEdgeDefs.Add(edgeDef);
                    }
                    break;
                default:
                    others.Add(contour);
                    break;
            }
        }

        // SketchContour subset-match: only meaningful with a known owning sketch
        // (segment id namespace is per-sketch).
        if (sourceSegIds.Count > 0 && owningSketch is not null) {
            var rawContours = owningSketch.GetSketchContours() as object[];
            if (rawContours is not null) {
                foreach (var ctrObj in rawContours) {
                    if (ctrObj is not SketchContour ctr) continue;
                    var ctrIds = CollectContourSegmentIds(ctr);
                    if (ctrIds.Count > 0 && ctrIds.IsSubsetOf(sourceSegIds)) {
                        results.Add(ctr);
                    }
                }
            }
        }

        // SketchRegion subset-match on geometric edge defs under tolerance.
        if (sourceEdgeDefs.Count > 0) {
            var sketches = owningSketch is not null
                ? new[] { owningSketch }
                : EnumerateSketches(file).ToArray();
            foreach (var sketch in sketches) {
                var rawRegions = sketch.GetSketchRegions() as object[];
                if (rawRegions is null) continue;
                foreach (var rgnObj in rawRegions) {
                    if (rgnObj is not SketchRegion region) continue;
                    if (RegionEdgeSubsetOf(region, sourceEdgeDefs)) {
                        results.Add(region);
                    }
                }
            }
        }

        // Non-contour/region (e.g. raw SketchSegment) falls through to per-definition resolver.
        foreach (var def in others) {
            var live = Resolve(file, def);
            if (live is not null) results.Add(live);
        }

        return results;
    }

    // Tolerance-based subset test: every live edge in region must match at least one source edge def via EdgeMatches.
    private static bool RegionEdgeSubsetOf(SketchRegion region, IReadOnlyList<Definition> sourceEdgeDefs) {
        var rawEdges = region.GetEdges() as object[];
        if (rawEdges is null || rawEdges.Length == 0) return false;
        foreach (var edgeObj in rawEdges) {
            if (edgeObj is not Edge edge) return false;
            var matched = false;
            foreach (var srcDef in sourceEdgeDefs) {
                // SketchRegionEdgeTol is looser than the strict 1e-7 — captured sketch-region
                // edges round-trip with float-noise z (e.g. 6.28e-7) from sketch-plane
                // projection, while live edges in the target sit cleanly at z=0.
                if (EdgeMatches(edge, srcDef, SketchRegionEdgeTol)) { matched = true; break; }
            }
            if (!matched) return false;
        }
        return true;
    }

    // Pass scopeSketch when the owning sketch is already known to skip the feature walk.
    internal static object ResolveSketchContourInSketch(File file, SketchContourDefinition def, Sketch? scopeSketch) {
        if (def.Segments is null || def.Segments.Count == 0) {
            throw new ArgumentException(
                "ResolveSketchContour: SketchContourDefinition.Segments must contain at least one segment");
        }
        // SW quirk: within a sketch, every COM subtype (SketchLine / SketchArc / ... /
        // SketchPoint) has its own GetID() namespace — the same int[2] can name several
        // entities. id.EntityKind picks one. Contour constituents are segments only.
        var liveSegments = new List<SketchSegment>(def.Segments.Count);
        Sketch? owningSketch = scopeSketch;
        foreach (var segDef in def.Segments) {
            var hostSketch = scopeSketch ?? FindSketchByFeatureName(file, segDef.Id.SketchName);
            if (hostSketch is null) {
                throw new InvalidOperationException(
                    $"ResolveSketchContour: no sketch named '{segDef.Id.SketchName}' for segment " +
                    $"({segDef.Id.EntityKind} {segDef.Id.Id})");
            }
            var seg = FindSketchSegmentInSketch(hostSketch, segDef.Id);
            if (seg is null) {
                throw new InvalidOperationException(
                    $"ResolveSketchContour: no {segDef.Id.EntityKind} SketchSegment with id " +
                    $"{segDef.Id.Id} in sketch '{segDef.Id.SketchName}'");
            }
            liveSegments.Add(seg);
            if (owningSketch is null) {
                owningSketch = hostSketch;
            } else if (((Feature)owningSketch).GetID() != ((Feature)hostSketch).GetID()) {
                throw new InvalidOperationException(
                    "ResolveSketchContour: segments span more than one sketch — a contour must live in a single sketch");
            }
        }
        if (owningSketch is null) {
            throw new InvalidOperationException("ResolveSketchContour: failed to identify owning sketch");
        }

        var capturedIds = BuildSegmentIdSet(liveSegments);

        // SketchContourDefinition is the segment-set flavor only; closed regions go through
        // RegionDefinition (ISketchRegion has no GetSketchSegments() accessor).
        var rawContours = owningSketch.GetSketchContours() as object[];
        if (rawContours is not null) {
            foreach (var ctrObj in rawContours) {
                if (ctrObj is SketchContour contour) {
                    var contourIds = CollectContourSegmentIds(contour);
                    if (SetEquals(capturedIds, contourIds)) return contour;
                }
            }
        }

        throw new InvalidOperationException(
            $"ResolveSketchContour: no SketchContour in sketch matches the captured " +
            $"set of {capturedIds.Count} segment id(s)");
    }

    private static HashSet<long> BuildSegmentIdSet(IEnumerable<SketchSegment> segments) {
        var ids = new HashSet<long>();
        foreach (var seg in segments) {
            var combined = SketchEntityId.CombineFrom(seg.GetID() as int[]);
            if (combined is null) continue;
            ids.Add(combined.Value);
        }
        return ids;
    }

    private static HashSet<long> CollectContourSegmentIds(SketchContour contour) {
        var ids = new HashSet<long>();
        var raw = contour.GetSketchSegments() as object[];
        if (raw is null) return ids;
        foreach (var obj in raw) {
            if (obj is SketchSegment seg) {
                var combined = SketchEntityId.CombineFrom(seg.GetID() as int[]);
                if (combined is not null) ids.Add(combined.Value);
            }
        }
        return ids;
    }

    private static bool SetEquals(HashSet<long> a, HashSet<long> b) {
        return a.Count == b.Count && a.SetEquals(b);
    }

    // -- Region resolution --------------------------------------------------

    // Match a region by set-equality on the canonical (sorted) serialized edge list of its bordering edges.
    private static SketchRegion? ResolveRegion(File file, RegionDefinition def) {
        var target = CanonicalEdgeKey(def.Edges);
        var matches = new List<SketchRegion>();
        foreach (var sketch in EnumerateSketches(file)) {
            var rawRegions = sketch.GetSketchRegions() as object[];
            if (rawRegions is null) continue;
            foreach (var rgnObj in rawRegions) {
                if (rgnObj is not SketchRegion region) continue;
                if (RegionEdgeKey(region) == target) matches.Add(region);
            }
        }
        return PickSingle(file, matches, "region");
    }

    private static string RegionEdgeKey(SketchRegion region) {
        var rawEdges = region.GetEdges() as object[];
        if (rawEdges is null) return CanonicalEdgeKey(Array.Empty<Definition>());
        var defs = new List<Definition>(rawEdges.Length);
        foreach (var edgeObj in rawEdges) {
            if (edgeObj is Edge edge) {
                defs.Add(DefinitionCapture.Capture(edge));
            }
        }
        return CanonicalEdgeKey(defs);
    }

    private static string CanonicalEdgeKey(IReadOnlyList<Definition> edges) {
        // SW quirk: GetEdges() order is not stable across rebuilds -- sort on serialized form.
        var serialized = new List<string>(edges.Count);
        foreach (var d in edges) {
            serialized.Add(d.ToJson().ToJsonString());
        }
        serialized.Sort(StringComparer.Ordinal);
        return string.Join("", serialized);
    }

    private static IEnumerable<Sketch> EnumerateSketches(File file) {
        var feat = file.ModelDoc.IFirstFeature();
        while (feat != null) {
            if (feat.GetSpecificFeature2() is Sketch sketch) {
                yield return sketch;
            }
            feat = feat.IGetNextFeature();
        }
    }

    // -- Helpers ------------------------------------------------------------

    private static IEnumerable<Body2> EnumerateBodies(File file) {
        // SW quirk: GetBodies2 lives on IPartDoc, not IModelDoc2, and only returns
        // one body type per call (no swAllBodies enum in this redist) — so iterate
        // solid + sheet + general types. Surface-extrude / offset-surface / split
        // produce SHEET bodies that own faces and edges feature payloads can
        // reference (e.g. extrude.contours[].edges anchored to an offset surface).
        // Filtering sheets out hides those refs from face/edge/vertex resolution.
        var doc = (IPartDoc)file.ModelDoc;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var bodyType in new[] {
            (int)swBodyType_e.swSolidBody,
            (int)swBodyType_e.swSheetBody,
            (int)swBodyType_e.swGeneralBody,
        }) {
            var raw = doc.GetBodies2(bodyType, true);
            if (raw is null) continue;
            foreach (Body2 body in (object[])raw) {
                // SW occasionally returns the same proxy under multiple bodyType
                // filters (visible/non-visible distinction inside one type). Dedup
                // by reference so the resolver doesn't multi-match against the
                // same body twice and trip PickSingle's "not unique" guard.
                if (seen.Add(body)) yield return body;
            }
        }
    }

    private static Feature? GetFeatureByName(ModelDoc2 modelDoc, string name) {
        var feat = modelDoc.IFirstFeature();
        while (feat != null) {
            if (feat.Name == name) return feat;
            feat = feat.IGetNextFeature();
        }
        // SW quirk: a shared sub-feature comes back through a consumer with a "<N>"
        // suffix indicating Nth consumer. Capture sometimes preserves that suffix; the
        // canonical top-level name has no suffix. Mirrors FeatureName.Resolve so any
        // FeatureDefinition consumer (Sweep path, axis name fallback, etc.) round-trips.
        var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"<\d+>$", "");
        if (stripped == name) return null;
        feat = modelDoc.IFirstFeature();
        while (feat != null) {
            if (feat.Name == stripped) return feat;
            feat = feat.IGetNextFeature();
        }
        return null;
    }

    // Resolves a TempAxisDefinition by SelectByID2("AXIS") at the midpoint of
    // the captured (start, end) and unwrapping to RefAxis. Critically does NOT
    // call ClearSelection — SelectByID2-recovered temp-axis proxies are bound
    // to the current selection and die when it's cleared. The caller (constraint
    // Add) consumes the proxy via DispatchWrapper into AddRelation, which happens
    // while the selection is still set; any later code that needs a clean
    // selection is responsible for clearing.
    private static RefAxis? ResolveTempAxis(File file, TempAxisDefinition def) {
        var ext = file.ModelDoc.Extension;
        var selMgr = file.ModelDoc.ISelectionManager;
        foreach (var pick in TempAxisPickPoints(def)) {
            file.ModelDoc.ClearSelection2(true);
            ext.SelectByID2(
                "", "AXIS", pick.X, pick.Y, pick.Z,
                /*append*/ false, /*mark*/ 0, /*callout*/ null,
                (int)swSelectOption_e.swSelectOptionExtensive);
            var axis = SelectedRefAxis(selMgr);
            if (axis is null) {
                SldworksLog.Information(
                    "ResolveTempAxis: pick=({X:G6},{Y:G6},{Z:G6}) selected no AXIS",
                    pick.X, pick.Y, pick.Z);
                continue;
            }
            if (TempAxisMatches(axis, def)) return axis;

            SldworksLog.Information(
                "ResolveTempAxis: pick=({X:G6},{Y:G6},{Z:G6}) selected non-matching AXIS live={Live} expected={Expected}",
                pick.X, pick.Y, pick.Z, AxisParamsForLog(axis), def.ToJson().ToJsonString());
        }
        file.ModelDoc.ClearSelection2(true);
        return null;
    }

    private static IEnumerable<Point3D> TempAxisPickPoints(TempAxisDefinition def) {
        yield return Lerp(def.Start, def.End, 0.5);
        yield return Lerp(def.Start, def.End, 0.25);
        yield return Lerp(def.Start, def.End, 0.75);
        yield return Lerp(def.Start, def.End, 0.1);
        yield return Lerp(def.Start, def.End, 0.9);
    }

    private static Point3D Lerp(Point3D a, Point3D b, double t) =>
        new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);

    private static RefAxis? SelectedRefAxis(SelectionMgr selMgr) {
        var sel = selMgr.GetSelectedObject6(1, 0);
        // SW 2026 returns the temp axis either as a RefAxis directly or as a
        // Feature wrapping it. Unwrap via GetSpecificFeature2 when needed.
        return sel switch {
            RefAxis ra => ra,
            Feature f  => f.GetSpecificFeature2() as RefAxis,
            _          => null,
        };
    }

    private static bool TempAxisMatches(RefAxis axis, TempAxisDefinition def) {
        if (axis.GetRefAxisParams() is not double[] p || p.Length < 6) return false;
        var liveStart = new Point3D(p[0], p[1], p[2]);
        var liveEnd = new Point3D(p[3], p[4], p[5]);
        var liveDir = new Direction(
            liveEnd.X - liveStart.X,
            liveEnd.Y - liveStart.Y,
            liveEnd.Z - liveStart.Z);
        var defDir = new Direction(
            def.End.X - def.Start.X,
            def.End.Y - def.Start.Y,
            def.End.Z - def.Start.Z);
        const double AxisLineTol = 1e-5;
        return DirectionsParallel(liveDir, defDir)
            && PointOnAxis(liveStart, def.Start, defDir, AxisLineTol)
            && PointOnAxis(liveEnd, def.Start, defDir, AxisLineTol);
    }

    private static string AxisParamsForLog(RefAxis axis) {
        if (axis.GetRefAxisParams() is not double[] p || p.Length < 6) return "<bad params>";
        return $"({p[0]:G6},{p[1]:G6},{p[2]:G6})->({p[3]:G6},{p[4]:G6},{p[5]:G6})";
    }

    // Axis is undirected — either orientation matches. Captured name is a tiebreaker for coaxial collisions.
    // A RefAxis is a FEATURE — resolve by NAME, always. The start/end displayed-extent
    // endpoints are echo-only: they drift across rebuilds (the visual extent depends on
    // surrounding geometry), so geometric matching gave 0 hits even when the axis
    // existed. NO geometry fallback — a name miss is a real
    // error (axis not created / renamed).
    private static Feature? ResolveRefAxis(ModelDoc2 modelDoc, RefAxisDefinition def) {
        var feat = modelDoc.IFirstFeature() as Feature;
        while (feat != null) {
            if (feat.Name == def.Name && IsRefAxisFeature(feat)) return feat;
            feat = feat.IGetNextFeature() as Feature;
        }
        return null;
    }

    private static bool IsRefAxisFeature(Feature feat) {
        return feat.GetTypeName2() == "RefAxis";
    }

    // A RefPlane is a FEATURE (default planes included) — resolve by NAME, always. The
    // X/Y/Z/origin axes are echo-only: SW doesn't pick a stable X/Y basis across
    // re-creates of a geometrically identical plane, so geometric matching is fragile.
    // NO geometry fallback — a name miss is a real error.
    private static Feature? ResolveRefPlane(File file, RefPlaneDefinition def) {
        var feat = file.ModelDoc.IFirstFeature() as Feature;
        while (feat != null) {
            if (feat.Name == def.Name && IsRefPlaneFeature(feat)) return feat;
            feat = feat.IGetNextFeature() as Feature;
        }
        return null;
    }

    private static bool IsRefPlaneFeature(Feature feat) {
        return feat.GetTypeName2() == "RefPlane";
    }

    private static T? PickSingle<T>(File file, List<T> matches, string label) where T : class {
        if (matches.Count == 0) return null;
        if (matches.Count > 1) {
            throw new InvalidOperationException(
                $"Resolve({label}): {matches.Count} matches under tol={Tol:G} — definition is not unique");
        }
        return matches[0];
    }

    private static Direction FaceNormal(Face2 face) {
        // SW quirk: IFace2.Normal evaluates at an unspecified parameter — only reliable for planes.
        var n = (double[])face.Normal;
        return new Direction(n[0], n[1], n[2]);
    }

    private static BoundingBox BodyBbox(Body2 body) {
        // SW quirk: GetExtremePoint returns the extreme point in the queried direction;
        // only the matching axis component is meaningful for the bbox.
        body.GetExtremePoint( 1,  0,  0, out var maxX, out _,        out _);
        body.GetExtremePoint(-1,  0,  0, out var minX, out _,        out _);
        body.GetExtremePoint( 0,  1,  0, out _,        out var maxY, out _);
        body.GetExtremePoint( 0, -1,  0, out _,        out var minY, out _);
        body.GetExtremePoint( 0,  0,  1, out _,        out _,        out var maxZ);
        body.GetExtremePoint( 0,  0, -1, out _,        out _,        out var minZ);
        return new BoundingBox(
            new Point3D(minX, minY, minZ),
            new Point3D(maxX, maxY, maxZ));
    }

    internal static Point3D ToPoint3D(double[] xyz) =>
        new(xyz[0], xyz[1], xyz.Length > 2 ? xyz[2] : 0.0);

    internal static bool PointWithinTol(Point3D a, Point3D b) => PointsWithin(a, b, Tol);

    internal static bool PointsWithin(Point3D a, Point3D b, double tol) {
        return Math.Abs(a.X - b.X) <= tol
            && Math.Abs(a.Y - b.Y) <= tol
            && Math.Abs(a.Z - b.Z) <= tol;
    }

    // SW quirk: axes flip direction across rebuilds, so compare |dot| ≈ 1.
    internal static bool DirectionsParallel(Direction a, Direction b) {
        var aLen = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        var bLen = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (aLen <= Tol || bLen <= Tol) return false;
        var dot = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (aLen * bLen);
        return Math.Abs(Math.Abs(dot) - 1.0) <= Tol;
    }

    private static bool PointOnAxis(Point3D point, Point3D lineOrigin, Direction lineDir, double? tol = null) {
        var dx = point.X - lineOrigin.X;
        var dy = point.Y - lineOrigin.Y;
        var dz = point.Z - lineOrigin.Z;
        var len = Math.Sqrt(lineDir.X * lineDir.X + lineDir.Y * lineDir.Y + lineDir.Z * lineDir.Z);
        if (len <= Tol) return false;
        var cx = dy * lineDir.Z - dz * lineDir.Y;
        var cy = dz * lineDir.X - dx * lineDir.Z;
        var cz = dx * lineDir.Y - dy * lineDir.X;
        var perp = Math.Sqrt(cx * cx + cy * cy + cz * cz) / len;
        return perp <= (tol ?? Tol);
    }
}
