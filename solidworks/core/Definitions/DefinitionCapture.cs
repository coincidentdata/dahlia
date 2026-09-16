using System.Text.Json.Nodes;
using Sldworks.Core.Handlers;
using Sldworks.Core.Handlers.Sketches.Entities;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
// Resolve the Point name collision: SW exposes SketchPoint/Point2D, our entity record is also Point.
using PointEntity = Sldworks.Core.Handlers.Sketches.Entities.Point;

namespace Sldworks.Core.Definitions;

internal static class DefinitionCapture {
    internal static Definition Capture(Face2 face) {
        var surface = (Surface?)face.IGetSurface();
        if (surface is null) {
            throw new InvalidOperationException(
                "Capture(Face2): face has no surface; cannot construct a Definition.");
        }

        // Parent body anchors the face to a specific Body2 — narrows resolution
        // when multiple bodies share a face flavor. Body identity is deterministic
        // across rebuilds (bbox via GetExtremePoint is exact to the bit, unlike
        // face's own bbox).
        var parentBody = CaptureParentBody(face);
        var onFacePoint = CaptureOnFacePoint(face, surface);

        if (surface.IsPlane()) {
            // SW quirk: IFace2.Normal evaluates at an unspecified parameter — only reliable for planes.
            var n = (double[])face.Normal;
            return new PlanarFaceDefinition(
                onFacePoint,
                new Direction(n[0], n[1], n[2]),
                parentBody);
        }

        if (surface.IsCylinder()) {
            // SW quirk: CylinderParams = [origin xyz, axis xyz, radius]; origin is any point
            // on the axis line, NOT the face centroid.
            var p = (double[])surface.CylinderParams;
            // Canonicalize axis sign — SW flips it across rebuilds.
            return new CylindricalFaceDefinition(
                new Point3D(p[0], p[1], p[2]),
                DeterministicAxis(p[3], p[4], p[5]),
                p[6],
                onFacePoint,
                parentBody);
        }

        if (surface.IsCone()) {
            // SW quirk: ConeParams2 = [apex xyz, axis xyz, radius_at_offset, half_angle].
            // Skip index 6 (radius varies between cones sharing an edge); half-angle is index 7.
            var p = (double[])surface.ConeParams2;
            // Canonicalize axis sign — SW flips it across rebuilds.
            return new ConicalFaceDefinition(
                new Point3D(p[0], p[1], p[2]),
                DeterministicAxis(p[3], p[4], p[5]),
                p[7],
                onFacePoint,
                parentBody);
        }

        if (surface.IsSphere()) {
            var p = (double[])surface.SphereParams;
            return new SphericalFaceDefinition(
                new Point3D(p[0], p[1], p[2]), p[3], onFacePoint, parentBody);
        }

        if (surface.IsTorus()) {
            // SW quirk: TorusParams = [center xyz, axis xyz, major_radius, minor_radius].
            var p = (double[])surface.TorusParams;
            // Canonicalize axis sign — SW flips it across rebuilds.
            return new ToroidalFaceDefinition(
                new Point3D(p[0], p[1], p[2]),
                DeterministicAxis(p[3], p[4], p[5]),
                p[6],
                p[7],
                onFacePoint,
                parentBody);
        }

        // Non-analytic surface — fall back to a multi-point on-face sample.
        return CaptureBSurfaceFace(face, surface, parentBody);
    }

    // Capture a single XYZ point guaranteed to lie on the face's trimmed extent.
    // Seed = UV-bbox midpoint XYZ; snap to trim via GetClosestPointOn so the result
    // sits on the trimmed face even for non-convex (L-shaped, donut) faces whose
    // UV-bbox midpoint falls in a hole. Resolver tests `face.GetClosestPointOn(p) ≈ p`,
    // which is invariant under SW's in-plane UV reparameterization (the failure
    // mode that defeats centroid-equality matching on tilted faces).
    private static Point3D CaptureOnFacePoint(Face2 face, Surface surface) {
        var seed = EvalFaceUVMid(face, surface);
        var snapped = (double[])face.GetClosestPointOn(seed[0], seed[1], seed[2]);
        return new Point3D(snapped[0], snapped[1], snapped[2]);
    }

    // 3x3 UV-grid of on-face samples for non-analytic surfaces. Each sample is
    // snapped to the face's trimmed extent via GetClosestPointOn so duplicates
    // are possible for tiny / off-trim seeds, but the match predicate handles
    // duplicates fine (each must pass GetClosestPointOn-on-target). Resolver
    // checks every sample lies on the candidate face — two different freeform
    // surfaces won't contain the same N points.
    private static BSurfaceFaceDefinition CaptureBSurfaceFace(Face2 face, Surface surface, BodyDefinition? parentBody) {
        // SW quirk: GetUVBounds = [minU, maxU, minV, maxV].
        var uv = (double[])face.GetUVBounds();
        double minU = uv[0], maxU = uv[1], minV = uv[2], maxV = uv[3];
        double midU = 0.5 * (minU + maxU), midV = 0.5 * (minV + maxV);

        var us = new[] { minU, midU, maxU };
        var vs = new[] { minV, midV, maxV };
        var pts = new List<Point3D>(9);
        foreach (var v in vs) {
            foreach (var u in us) {
                var seed = (double[])surface.Evaluate(u, v, 0, 0);
                var snapped = (double[])face.GetClosestPointOn(seed[0], seed[1], seed[2]);
                pts.Add(new Point3D(snapped[0], snapped[1], snapped[2]));
            }
        }
        return new BSurfaceFaceDefinition(pts, parentBody);
    }

    private static double[] EvalFaceUVMid(Face2 face, Surface surface) {
        var uv = (double[])face.GetUVBounds();
        double midU = 0.5 * (uv[0] + uv[1]);
        double midV = 0.5 * (uv[2] + uv[3]);
        return (double[])surface.Evaluate(midU, midV, 0, 0);
    }

    private static BodyDefinition? CaptureParentBody(Face2 face) {
        // SW quirk: every Face2 belongs to exactly one Body2 — IGetBody never null
        // on a valid face. Capture it as a deterministic anchor.
        var body = face.IGetBody();
        return body is null ? null : Capture(body);
    }

    private static BodyDefinition? CaptureParentBody(Edge edge) {
        // SW quirk: IEdge.GetBody returns the owning body. Required for resolution
        // when multiple bodies share an edge with identical geometric identity.
        if (edge.GetBody() is not Body2 body) return null;
        return Capture(body);
    }

    private static BodyDefinition? CaptureParentBody(Vertex vertex) {
        // SW quirk: IVertex has no GetBody — fish via an adjacent face. A vertex on
        // a manifold body always has at least one adjacent face (every vertex
        // bounds an edge that bounds a face).
        if (vertex.GetAdjacentFaces() is not object[] faces || faces.Length == 0) return null;
        if (((Face2)faces[0]).IGetBody() is not Body2 body) return null;
        return Capture(body);
    }

    internal static Definition Capture(Edge edge) {
        // SW quirk: Edge.Check exposes kernel faults; a faulty edge still exposes a curve
        // and vertices, so without this we'd capture a Definition that can never resolve.
        var fault = edge.Check;
        if (fault.Count > 0) {
            var codes = new List<int>(fault.Count);
            for (var i = 0; i < fault.Count; i++) codes.Add(fault.ErrorCode[i]);
            throw new InvalidOperationException(
                $"Capture(Edge): SolidWorks reports {fault.Count} fault(s) on edge: " +
                $"swFaultEntityErrorCode_e=[{string.Join(',', codes)}].");
        }

        var curve = (Curve?)edge.IGetCurve();
        if (curve is null) {
            throw new InvalidOperationException(
                "Capture(Edge): edge has no curve; cannot construct a Definition.");
        }

        var parentBody = CaptureParentBody(edge);

        if (curve.IsLine()) {
            var s = edge.IGetStartVertex();
            var e = edge.IGetEndVertex();
            Point3D p0, p1;
            // Missing vertices (typical of infinite construction lines) — evaluate the curve at param ends.
            if (s is not null && e is not null) {
                p0 = DefinitionResolver.ToPoint3D((double[])s.GetPoint());
                p1 = DefinitionResolver.ToPoint3D((double[])e.GetPoint());
            } else {
                curve.GetEndParams(out var u0, out var u1, out _, out _);
                p0 = DefinitionResolver.ToPoint3D((double[])curve.Evaluate2(u0, 0));
                p1 = DefinitionResolver.ToPoint3D((double[])curve.Evaluate2(u1, 0));
            }
            // Line is undirected; sort lex so the JSON key is rebuild-stable.
            (p0, p1) = OrderEndpoints(p0, p1);
            return new LineEdgeDefinition(p0, p1, parentBody);
        }

        if (curve.IsCircle()) {
            // SW quirk: IsCircle() is true for closed circles AND circular arcs. Disambiguate
            // via start/end vertices (null on closed edges, non-null on arcs) so a 180° arc
            // doesn't collide with a full circle of the same center / axis / radius.
            var p = (double[])curve.CircleParams;
            var sv = edge.IGetStartVertex();
            var ev = edge.IGetEndVertex();
            Point3D? arcStart = null;
            Point3D? arcEnd = null;
            if (sv is not null && ev is not null) {
                var a = DefinitionResolver.ToPoint3D((double[])sv.GetPoint());
                var b = DefinitionResolver.ToPoint3D((double[])ev.GetPoint());
                // Arc is orientation-agnostic — sort lex for stable JSON keying.
                (arcStart, arcEnd) = OrderEndpoints(a, b);
            }
            // Canonicalize axis sign — SW flips it across rebuilds.
            var axis = DeterministicAxis(p[3], p[4], p[5]);
            return new CircularEdgeDefinition(
                new Point3D(p[0], p[1], p[2]),
                axis,
                p[6],
                arcStart,
                arcEnd,
                parentBody);
        }

        if (curve.IsEllipse()) {
            // SW quirk: GetEllipseParams = [center xyz, major_radius, major_axis xyz,
            // minor_radius, minor_axis xyz]. IsEllipse() is true for closed ellipses AND
            // elliptical arcs; disambiguate via start/end vertices (null on closed edges).
            var p = (double[])curve.GetEllipseParams();
            var sv = edge.IGetStartVertex();
            var ev = edge.IGetEndVertex();
            Point3D? arcStart = null;
            Point3D? arcEnd = null;
            if (sv is not null && ev is not null) {
                var a = DefinitionResolver.ToPoint3D((double[])sv.GetPoint());
                var b = DefinitionResolver.ToPoint3D((double[])ev.GetPoint());
                (arcStart, arcEnd) = OrderEndpoints(a, b);
            }
            // Both axes flip sign independently across rebuilds — canonicalize each.
            var major = DeterministicAxis(p[4], p[5], p[6]);
            var minor = DeterministicAxis(p[8], p[9], p[10]);
            return new EllipticalEdgeDefinition(
                new Point3D(p[0], p[1], p[2]),
                major,
                minor,
                p[3],
                p[7],
                arcStart,
                arcEnd,
                parentBody);
        }

        if (curve.IsBcurve()) {
            // SW quirk: IsBcurve() is true for genuine splines AND helix edges; helix
            // start/end may collide on the same SplineEdgeDefinition (resolver throws on multi-match).
            return CaptureSpline(curve, parentBody);
        }

        // Parabola / other exotic curve types — no dedicated flavor; sampling as a generic
        // spline would not round-trip (resolver only walks IsBcurve() edges).
        throw new NotSupportedException(
            $"Capture(Edge): unsupported curve type '{curve.Identity()}'. " +
            "Line / circular / elliptical / spline edges have Definition flavors today.");
    }

    // SW hands endpoint vertices back in non-deterministic order across rebuilds; lex-order gives a stable JSON key.
    private static (Point3D, Point3D) OrderEndpoints(Point3D a, Point3D b) {
        return ComparePoint3D(a, b) <= 0 ? (a, b) : (b, a);
    }

    private static int ComparePoint3D(Point3D a, Point3D b) {
        var c = a.X.CompareTo(b.X);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        if (c != 0) return c;
        return a.Z.CompareTo(b.Z);
    }

    // Canonicalize an axis vector's sign so a flipped axis gets the same JSON key: flip to a positive-leading-component sign on the first dimension whose magnitude exceeds 10E-8.
    private static Direction DeterministicAxis(double x, double y, double z) {
        const double tieEps = 10E-8;
        var v = new[] { x, y, z };
        for (var i = 0; i < 3; i++) {
            if (Math.Abs(v[i]) < tieEps) continue;
            if (v[i] < 0) return new Direction(-x, -y, -z);
            break;
        }
        return new Direction(x, y, z);
    }

    private static SplineEdgeDefinition CaptureSpline(Curve curve, BodyDefinition? parentBody) {
        const int steps = 8;
        var pts = new List<Point3D>(steps + 1);
        curve.GetEndParams(out var u0, out var u1, out var isClosed, out _);
        for (var i = 0; i <= steps; i++) {
            var t = u0 + (u1 - u0) * i / steps;
            var p = (double[])curve.Evaluate2(t, 0);
            pts.Add(new Point3D(p[0], p[1], p[2]));
        }

        // SW quirk: closed splines can flip parameter direction across rebuilds, so canonicalize
        // by picking a deterministic order based on the first dimension whose entries differ by >10E-8.
        if (ShouldFlipSpline(pts, isClosed)) {
            pts.Reverse();
        }
        return new SplineEdgeDefinition(pts, Knots: [], Degree: 3, Periodic: isClosed, ParentBody: parentBody);
    }

    private static bool ShouldFlipSpline(List<Point3D> pts, bool isClosed) {
        if (pts.Count < 2) return false;
        const double tieEps = 10E-8;
        if (isClosed) {
            if (pts.Count < 3) return false;
            var start = pts[0];
            var dirA = pts[1];
            var dirB = pts[pts.Count - 2];
            var vA = new[] { dirA.X - start.X, dirA.Y - start.Y, dirA.Z - start.Z };
            var vB = new[] { dirB.X - start.X, dirB.Y - start.Y, dirB.Z - start.Z };
            for (var i = 0; i < 3; i++) {
                if (Math.Abs(vA[i] - vB[i]) < tieEps) continue;
                return vA[i] < vB[i];
            }
            return false;
        } else {
            var start = pts[0];
            var end = pts[pts.Count - 1];
            var s = new[] { start.X, start.Y, start.Z };
            var e = new[] { end.X, end.Y, end.Z };
            for (var i = 0; i < 3; i++) {
                if (Math.Abs(s[i] - e[i]) < tieEps) continue;
                return s[i] < e[i];
            }
            return false;
        }
    }

    internal static VertexDefinition Capture(Vertex vertex) {
        var p = (double[])vertex.GetPoint();
        return new VertexDefinition(new Point3D(p[0], p[1], p[2]), CaptureParentBody(vertex));
    }

    internal static BodyDefinition Capture(Body2 body) {
        var (centroid, volume, surfaceArea) = MassProperties.Compute(body);

        // SW quirk: GetExtremePoint returns the extreme point in the queried direction;
        // only the matching axis component is meaningful (other components are arbitrary
        // surface coords, NOT bbox bounds). Take each axis's own component to assemble corners.
        body.GetExtremePoint( 1,  0,  0, out var maxX, out _,    out _);
        body.GetExtremePoint(-1,  0,  0, out var minX, out _,    out _);
        body.GetExtremePoint( 0,  1,  0, out _,        out var maxY, out _);
        body.GetExtremePoint( 0, -1,  0, out _,        out var minY, out _);
        body.GetExtremePoint( 0,  0,  1, out _,        out _,    out var maxZ);
        body.GetExtremePoint( 0,  0, -1, out _,        out _,    out var minZ);
        var bbox = new BoundingBox(
            new Point3D(minX, minY, minZ),
            new Point3D(maxX, maxY, maxZ));

        return new BodyDefinition(centroid, volume, bbox, surfaceArea);
    }

    // Convenience overload that fetches the owning sketch's name on demand. Costs a
    // GetSketch() round-trip per entity — prefer the (segment, sketchName) overload
    // in hot loops (SketchHandler.Inspect grabs the name once for the whole sketch).
    internal static SketchEntityDefinition Capture(SketchSegment segment) =>
        Capture(segment, OwningSketchName(segment));

    internal static PointEntity Capture(SketchPoint point) =>
        Capture(point, OwningSketchName(point));

    private static string OwningSketchName(object entity) {
        var owner = entity switch {
            ISketchSegment seg => seg.GetSketch(),
            ISketchPoint pt    => pt.GetSketch(),
            _ => null,
        };
        if (owner is Feature feat) return feat.Name ?? "";
        throw new InvalidOperationException(
            "Capture(sketch entity): owning sketch is not a Feature; cannot resolve sketch name");
    }

    // Dispatch by SW segment type to the matching SketchEntityDefinition subclass.
    // Caller passes sketchName (owning sketch's Feature.Name) — fetching it here would
    // round-trip GetSketch() per entity in a hot loop; SketchHandler does it once.
    internal static SketchEntityDefinition Capture(SketchSegment segment, string sketchName) {
        var id = SketchEntityIdFor(segment, sketchName);
        return segment switch {
            SketchLine     => Line.Parse(segment, sketchName) with { Id = id },
            // SW quirk: SketchArc covers both circles and arcs; route by IsCircle().
            SketchArc arc when arc.IsCircle() != 0 => Circle.Parse(segment, sketchName) with { Id = id },
            SketchArc      => Arc.Parse(segment, sketchName) with { Id = id },
            // SW quirk: SketchEllipse covers full ellipses AND elliptical arcs — see
            // Ellipse.cs / EllipticalArc.cs for the start/end point-equality split.
            SketchEllipse el when EllipseIsClosed(el) => Ellipse.Parse(segment, sketchName) with { Id = id },
            SketchEllipse  => EllipticalArc.Parse(segment, sketchName) with { Id = id },
            SketchParabola => Parabola.Parse(segment, sketchName) with { Id = id },
            SketchSpline   => Spline.Parse(segment, sketchName) with { Id = id },
            _ => throw new NotSupportedException(
                // SW quirk: SketchSegment.GetType() returns a swSketchType_e int (NOT the
                // .NET runtime type); reach Object.GetType() via the object cast.
                $"Capture(SketchSegment): unsupported segment runtime type {((object)segment).GetType().Name}"),
        };
    }

    internal static PointEntity Capture(SketchPoint pt, string sketchName) {
        return PointEntity.Parse(pt, sketchName) with { Id = SketchEntityIdFor(pt, sketchName) };
    }

    private static bool EllipseIsClosed(SketchEllipse el) {
        // SW quirk: a closed SketchEllipse and an open elliptical arc share swSketchELLIPSE.
        // Disambiguate by tolerance-based start==end equality (more robust than reference
        // equality against round-trip noise).
        const double tol = 1e-9;
        var s = el.IGetStartPoint2();
        var e = el.IGetEndPoint2();
        if (s is null || e is null) return true;
        return Math.Abs(s.X - e.X) < tol && Math.Abs(s.Y - e.Y) < tol && Math.Abs(s.Z - e.Z) < tol;
    }

    internal static SketchEntityId SketchEntityIdFor(object entity, string sketchName) {
        (string kind, object? raw) = entity switch {
            ISketchPoint pt    => (SketchEntityId.KindPoint, pt.GetID()),
            SketchLine line    => (SketchEntityId.KindLine, ((ISketchSegment)line).GetID()),
            // SketchArc covers both circles and arcs; both share the "arc" id namespace.
            SketchArc arc      => (SketchEntityId.KindArc, ((ISketchSegment)arc).GetID()),
            // SketchEllipse covers both closed ellipses and elliptical arcs; same namespace.
            SketchEllipse el   => (SketchEntityId.KindEllipse, ((ISketchSegment)el).GetID()),
            SketchParabola par => (SketchEntityId.KindParabola, ((ISketchSegment)par).GetID()),
            SketchSpline sp    => (SketchEntityId.KindSpline, ((ISketchSegment)sp).GetID()),
            _ => throw new InvalidOperationException(
                $"Capture(sketch entity): unsupported runtime type {entity.GetType().Name} " +
                "(expected ISketchPoint or a SketchSegment subtype Line/Arc/Ellipse/Parabola/Spline)"),
        };
        if (raw is int[] ids && ids.Length >= 2) {
            return new SketchEntityId(sketchName, kind, SketchEntityId.Combine(ids[0], ids[1]));
        }
        throw new InvalidOperationException(
            "Capture(sketch entity): GetID() returned an unexpected shape; cannot build a SketchEntityId");
    }

    internal static SketchContourDefinition Capture(SketchContour contour) {
        var raw = contour.GetSketchSegments() as object[]
            ?? throw new InvalidOperationException(
                "Capture(SketchContour): GetSketchSegments returned null or non-array");
        var segs = new List<SketchEntityDefinition>(raw.Length);
        foreach (var obj in raw) {
            if (obj is SketchSegment seg) {
                segs.Add(Capture(seg));
            } else {
                throw new InvalidOperationException(
                    $"Capture(SketchContour): segment entry is unexpected type {obj?.GetType().Name ?? "null"}");
            }
        }
        if (segs.Count == 0) {
            throw new InvalidOperationException("Capture(SketchContour): contour has no segments");
        }
        // SW quirk: GetSketchSegments() order is not stable across rebuilds — sort by id pair
        // for a rebuild-stable JSON key (resolver matches by set-equality, not order).
        segs.Sort(CompareSketchEntityById);
        return new SketchContourDefinition(segs);
    }

    private static int CompareSketchEntityById(SketchEntityDefinition a, SketchEntityDefinition b) {
        // Lex sort by (SketchName, EntityKind, Id) for a rebuild-stable JSON key.
        var c = string.CompareOrdinal(a.Id.SketchName, b.Id.SketchName);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Id.EntityKind, b.Id.EntityKind);
        if (c != 0) return c;
        return a.Id.Id.CompareTo(b.Id.Id);
    }

    internal static RegionDefinition Capture(SketchRegion region) {
        // SW quirk: ISketchRegion exposes GetEdges() but no GetSketchSegments(), so define
        // the region by its bordering edges and let the resolver match by edge-set equality.
        var rawEdges = region.GetEdges() as object[]
            ?? throw new InvalidOperationException(
                "Capture(SketchRegion): GetEdges returned null or non-array");
        var edgeDefs = new List<Definition>(rawEdges.Length);
        foreach (var edgeObj in rawEdges) {
            if (edgeObj is null) continue;
            if (edgeObj is Edge edge) {
                edgeDefs.Add(Capture(edge));
            } else {
                throw new InvalidOperationException(
                    $"Capture(SketchRegion): edge entry is unexpected type {edgeObj.GetType().Name} " +
                    "(expected Edge)");
            }
        }
        if (edgeDefs.Count == 0) {
            throw new InvalidOperationException(
                "Capture(SketchRegion): region has no bordering edges");
        }
        // SW quirk: region.GetEdges() order is not stable across rebuilds — sort by per-edge
        // JSON for a rebuild-stable RegionDefinition.Edges (per-edge JSON is already canonical).
        edgeDefs.Sort((a, b) => string.CompareOrdinal(
            a.ToJson().ToJsonString(), b.ToJson().ToJsonString()));

        // Bake an interior witness point. Mandatory: the round-trip runner
        // probe-swaps every region into a target.probe_region(parent_sketch,
        // interior) call so the transcript can re-issue the same probe. A
        // null interior point breaks that pipeline, so capture must fail loudly
        // here rather than ship an unresolvable region.
        var owningSketch = region.Sketch
            ?? throw new InvalidOperationException(
                "Capture(SketchRegion): region.Sketch is null — can't sample loops " +
                "for interior-point computation");
        var loops = SketchRegionGeometry.SampleLoops(region, owningSketch);
        if (loops.Count == 0) {
            throw new InvalidOperationException(
                "Capture(SketchRegion): SampleLoops returned 0 polylines — " +
                "region has no traversable boundary (coedges may be unrecoverable)");
        }
        var interior = SketchRegionGeometry.ComputeInteriorPoint(loops);
        return new RegionDefinition(edgeDefs, interior);
    }


    internal static Definition Capture(Feature feature) {
        // Reach the typed proxy via GetSpecificFeature2 so we produce a geometric Definition
        // (disambiguates same-named axes/planes) instead of a name-only FeatureDefinition.
        var typeName = feature.GetTypeName2();
        if (typeName == "RefAxis" && feature.GetSpecificFeature2() is RefAxis axis) {
            return Capture(axis, feature);
        }
        // Default planes ("Top Plane" etc.) are also RefPlane features; geometry-based
        // matching sidesteps locale-dependent default-plane names.
        if (typeName == "RefPlane" && feature.GetSpecificFeature2() is RefPlane plane) {
            return Capture(plane, feature);
        }
        return new FeatureDefinition(feature.Name);
    }

    // Temp axes have no durable per-rebuild handle, so identity rides on a
    // model-space pick point ON the axis (midpoint of GetRefAxisParams). The
    // proxy is bound to the current selection and dies on ClearSelection, so
    // capture MUST extract everything synchronously before the caller clears.
    internal static Definition Capture(RefAxis axis) {
        if (axis.IsTempAxis()) {
            return CaptureTempAxis(axis);
        }
        // SW quirk: a user-created RefAxis COM coclass implements both IRefAxis and IFeature,
        // so direct-cast for the name. Must come after the IsTempAxis branch (temp axes do NOT implement IFeature).
        var feature = (Feature)axis;
        return Capture(axis, feature);
    }

    internal static Definition Capture(RefAxis axis, Feature feature) {
        if (axis.IsTempAxis()) {
            return CaptureTempAxis(axis);
        }
        // SW quirk: GetRefAxisParams = [startX..Z, endX..Z] in METERS regardless of doc units.
        var p = (double[])axis.GetRefAxisParams();
        if (p is null || p.Length < 6) {
            throw new InvalidOperationException(
                $"Capture(RefAxis): GetRefAxisParams returned an unexpected shape for feature '{feature.Name}'.");
        }
        // RefAxis is undirected; sort lex so the captured JSON key is rebuild-stable.
        var (s, e) = OrderEndpoints(
            new Point3D(p[0], p[1], p[2]),
            new Point3D(p[3], p[4], p[5]));
        return new RefAxisDefinition(feature.Name, s, e);
    }

    // Wire identity for a temp axis = (start, end) endpoints of its displayed
    // extent (GetRefAxisParams). Sort lex so the JSON key is rebuild-stable;
    // resolver picks the midpoint via SelectByID2("AXIS").
    private static TempAxisDefinition CaptureTempAxis(RefAxis axis) {
        if (axis.GetRefAxisParams() is not double[] p || p.Length < 6) {
            throw new InvalidOperationException(
                "Capture(temp axis): GetRefAxisParams returned an unexpected shape");
        }
        var (s, e) = OrderEndpoints(
            new Point3D(p[0], p[1], p[2]),
            new Point3D(p[3], p[4], p[5]));
        return new TempAxisDefinition(s, e);
    }

    // Capture as X/Y/Z basis + origin derived from plane.Transform.IInverse(). Owning feature name is a tiebreaker.
    internal static RefPlaneDefinition Capture(RefPlane plane) {
        // SW quirk: a RefPlane COM coclass implements both IRefPlane and IFeature.
        var feature = (Feature)plane;
        return Capture(plane, feature);
    }

    private static RefPlaneDefinition Capture(RefPlane plane, Feature feature) {
        var axes = MathUtils.GetTransformMatrix(plane.Transform.IInverse());
        return new RefPlaneDefinition(
            feature.Name,
            new Direction(axes[0][0], axes[0][1], axes[0][2]),
            new Direction(axes[1][0], axes[1][1], axes[1][2]),
            new Direction(axes[2][0], axes[2][1], axes[2][2]),
            new Point3D(axes[3][0], axes[3][1], axes[3][2]));
    }

    // Order matters: SW COM proxies implement multiple interfaces on one coclass.
    // Sketch geometry comes first (no ambiguity — sketch types don't QI to Face2 /
    // Feature). Then Feature (RefAxis / RefPlane / temp-axis-wrapped-as-Feature) —
    // SW 2026 spuriously satisfies `is Face2` for some Feature COM objects, so
    // checking Feature FIRST routes them through Capture(Feature) which uses
    // GetSpecificFeature2() to disambiguate. Body topology last (these don't
    // expose IFeature, so they fall through cleanly).
    internal static Definition? Capture(object? entity) {
        return entity switch {
            null                  => null,
            SketchSegment seg     => Capture(seg),
            SketchPoint pt        => Capture(pt),
            SketchContour contour => Capture(contour),
            SketchRegion region   => Capture(region),
            RefAxis axis          => Capture(axis),
            RefPlane plane        => Capture(plane),
            Feature feature       => Capture(feature),
            Face2 face            => Capture(face),
            Edge edge             => Capture(edge),
            Vertex vertex         => Capture(vertex),
            Body2 body            => Capture(body),
            _                     => null,
        };
    }
}
