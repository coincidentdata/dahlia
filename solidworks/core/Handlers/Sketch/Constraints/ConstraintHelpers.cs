using System.Text.Json.Nodes;
using Sldworks.Core;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Sketches;

// Cross-cutting helpers used by every constraint family. ResolveTextLocation projects sketch-plane
// text positions to model space (Add path); ReadRelationRefs walks SketchRelation.GetDefinitionEntities2()
// and emits ref JSON, including the Revolve/Cut-Revolve temp-axis recovery (Read path).
internal static class ConstraintHelpers {
    // echo_text_location is sketch-plane (x, y); project to model space via ModelToSketchTransform.IInverse. Display-only; falls back to (0,0,0).
    internal static (double tx, double ty, double tz) ResolveTextLocation(File file, Sketch sketch, JsonNode node) {
        double tx = 0, ty = 0, tz = 0;
        if (node["echo_text_location"] is JsonArray la && la.Count >= 2) {
            var sx = la[0]?.GetValue<double>() ?? 0;
            var sy = la[1]?.GetValue<double>() ?? 0;
            var inv = ((ISketch)sketch).ModelToSketchTransform?.IInverse();
            if (inv is not null) {
                var mp = (MathPoint)file.MathUtil.CreatePoint(new[] { sx, sy, 0.0 });
                var coords = (double[])mp.IMultiplyTransform(inv).ArrayData;
                tx = coords[0]; ty = coords[1]; tz = coords.Length > 2 ? coords[2] : 0;
            } else {
                tx = sx; ty = sy;
            }
        }
        return (tx, ty, tz);
    }

    internal static JsonArray? ReadRelationRefs(File file, SketchRelation relation, Sketch owningSketch,
                                                HashSet<(string Kind, long Id)> emittedIds) {
        var ents = relation.GetEntitiesCount();
        if (ents <= 0) return null;
        var refs = new JsonArray();
        // SW quirk: relation.GetEntities() proxies cross-sketch entities as belonging to
        // the ACTIVE sketch via GetSketch(); GetDefinitionEntities2() returns proxies whose
        // GetSketch() truthfully reports the owning sketch — always use the `2` variant
        // here. DefinitionCapture.Capture(seg) reads GetSketch().Name internally, so
        // SketchEntityId.SketchName is correct without an override layer.
        if (relation.GetDefinitionEntities2() is not object[] entities) return null;
        // SW quirk: for a Revolve/Cut-Revolve proxy at slot i, GetEntities()[i] is the underlying SketchSegment used to recover the implicit temp axis (lazy-fetched).
        object[]? rawEntities = null;
        // SW quirk: a body silhouette edge / vertex projected onto the sketch plane appears
        // in GetDefinitionEntities2() as a SketchSegment / SketchPoint COM proxy whose ID
        // doesn't belong to the owning sketch's emitted entity list — capturing it emits a
        // sketch_line / sketch_point ref with a bogus id that ResolveSketchEntityId can't
        // resolve. `emittedIds` is the set the caller built once for this sketch.
        var owningSketchName = OwningSketchName(owningSketch);
        for (var i = 0; i < entities.Length; i++) {
            var entity = entities[i];

            switch (entity) {
                case SketchSegment seg: {
                    // Only enforce the wire-emission gate when the ref belongs to the SAME
                    // sketch we're capturing constraints for. Cross-sketch refs resolve via
                    // their own sketch_name on the target side — trust them.
                    if (RefBelongsToSameSketch(seg, owningSketchName)) {
                        var key = TryRefKey(seg, owningSketchName);
                        if (key is null || !emittedIds.Contains(key.Value)) {
                            var segId = ((ISketchSegment)seg).GetID() as int[];
                            SldworksLog.Warning(
                                "Inspect(Sketch): skipping constraint — refs a SketchSegment " +
                                "(id=[{Id0},{Id1}]) that the wire format doesn't emit " +
                                "(body silhouette projection); constraint will be lost on round-trip",
                                segId is null ? -1 : segId[0], segId is null ? -1 : segId[1]);
                            return null;
                        }
                    }
                    refs.Add(DefinitionCapture.Capture(seg).ToJson());
                    continue;
                }
                case SketchPoint pt: {
                    if (RefBelongsToSameSketch(pt, owningSketchName)) {
                        var key = TryRefKey(pt, owningSketchName);
                        if (key is null || !emittedIds.Contains(key.Value)) {
                            var ptId = pt.GetID() as int[];
                            SldworksLog.Warning(
                                "Inspect(Sketch): skipping constraint — refs a SketchPoint " +
                                "(id=[{Id0},{Id1}], type={Type}) that the wire format doesn't emit " +
                                "(curve endpoint / arc-center / ellipse-axis / silhouette marker); " +
                                "constraint will be lost on round-trip",
                                ptId is null ? -1 : ptId[0], ptId is null ? -1 : ptId[1], pt.Type);
                            return null;
                        }
                    }
                    refs.Add(DefinitionCapture.Capture(pt).ToJson());
                    continue;
                }
            }

            // Revolve/Cut-Revolve Feature proxy => recover implicit temp axis from the matching raw SketchLine via SelectByID2("AXIS").
            // SW quirk: must normalize through ResolveFeatureTypeName — the bare
            // GetTypeName2() can return legacy short forms (e.g. "RevCut" instead
            // of "Cut-Revolve") that don't match the SwTypeName constants here.
            if (entity is Feature feat) {
                var typeName = File.ResolveFeatureTypeName(feat);
                if (typeName == RevolveHandler.SwTypeName /* "Revolution" */ ||
                    typeName == CutRevolveHandler.SwTypeName /* "Cut-Revolve" */) {
                    rawEntities ??= relation.GetEntities() as object[];
                    if (rawEntities is not null && i < rawEntities.Length) {
                        var axisDef = RecoverTempAxisDefFromRawEntity(
                            file, owningSketch, rawEntities[i],
                            requireEndpointMatch: true);
                        if (axisDef is not null) {
                            refs.Add(axisDef.ToJson());
                            continue;
                        }
                    }
                    SldworksLog.Warning(
                        "Inspect(Sketch): skipping constraint — Revolve/Cut-Revolve proxy '{Name}' " +
                        "at slot {Slot} did not yield a recoverable temp axis",
                        feat.Name, i);
                    return null;
                }
            }

            // Curved-face (cylinder/cone/torus) ref in a sketch constraint may
            // stand in for the face's TEMP AXIS — SW reports the defining face
            // for derived axes (`GetDefinitionEntities2`), and the no-`2`
            // `GetEntities` reveals an internal SketchPoint / SketchLine as the
            // axis anchor. Recover the live temp axis and wrap as TempAxisDefinition.
            // Fall through to generic Face2 capture if recovery fails.
            if (entity is Face2 face && IsCylindricalOrConical(face)) {
                rawEntities ??= relation.GetEntities() as object[];
                var rawEntity = rawEntities is not null && i < rawEntities.Length
                    ? rawEntities[i]
                    : null;
                SldworksLog.Information(
                    "Inspect(Sketch): {SketchName} relationType={RelationType} " +
                    "curved-face constraint ref slot {Slot} " +
                    "definitionEntity={DefinitionEntityType} rawEntity={RawEntityType}; " +
                    "trying temp-axis recovery",
                    owningSketchName, relation.GetRelationType(),
                    i, TypeLabel(entity), TypeLabel(rawEntity));

                if (rawEntity is not null) {
                    var axisDef = RecoverTempAxisDefFromCurvedFaceRawEntity(
                        file, owningSketch, face, rawEntity);
                    if (axisDef is not null) {
                        SldworksLog.Information(
                            "Inspect(Sketch): {SketchName} relationType={RelationType} " +
                            "curved-face constraint ref slot {Slot} " +
                            "recovered temp-axis definition {AxisDefinition}",
                            owningSketchName, relation.GetRelationType(),
                            i, axisDef.ToJson().ToJsonString());
                        refs.Add(axisDef.ToJson());
                        continue;
                    }
                }
                SldworksLog.Information(
                    "Inspect(Sketch): {SketchName} relationType={RelationType} " +
                    "curved-face constraint ref slot {Slot} " +
                    "did not recover temp axis; falling through to generic DefinitionCapture",
                    owningSketchName, relation.GetRelationType(), i);
            }

            // Everything else — Edge / Face2 / Vertex from parent body, RefAxis, generic
            // Feature, etc. — routes through DefinitionCapture's `Capture(object?)`
            // dispatcher. The wire format carries arbitrary Definitions in constraint refs
            // (SketchConstraint.ResolveRef -> Definition.FromJson -> DefinitionResolver.Resolve),
            // and SW's IRelationManager.AddRelation accepts a heterogeneous reference
            // array — sketch entities AND body topology — so capturing as a
            // LineEdgeDefinition / VertexDefinition / PlanarFaceDefinition / etc. flows
            // through end-to-end. Drop only when Capture itself can't represent the
            // entity (SilhouetteEdge is the live case — no wire flavor for it yet).
            var captured = DefinitionCapture.Capture(entity);
            if (captured is not null) {
                refs.Add(captured.ToJson());
                continue;
            }
            var typeLabel = entity switch {
                SilhouetteEdge => "SilhouetteEdge",
                null           => "null",
                _              => Microsoft.VisualBasic.Information.TypeName(entity),
            };
            SldworksLog.Warning(
                "Inspect(Sketch): skipping constraint — ref of type {Type} has no Definition encoding",
                typeLabel);
            return null;
        }
        return refs;
    }

    private static Definition? RecoverTempAxisDefFromCurvedFaceRawEntity(
        File file, Sketch sketch, Face2 face, object? raw) {
        if (raw is SketchLine line) {
            var axisDef = RecoverTempAxisDefFromFaceAnchorLine(file, sketch, face, line);
            if (axisDef is not null) return axisDef;
        }
        return RecoverTempAxisDefFromRawEntity(
            file, sketch, raw,
            requireEndpointMatch: false);
    }

    private static string TypeLabel(object? entity) => entity switch {
        SketchLine       => "SketchLine",
        SketchPoint      => "SketchPoint",
        SketchSegment    => "SketchSegment",
        Face2 face when IsCylindricalOrConical(face) => "curved Face2",
        Face2            => "Face2",
        Edge             => "Edge",
        Vertex           => "Vertex",
        RefAxis          => "RefAxis",
        Feature feat     => $"Feature({feat.Name}, {File.ResolveFeatureTypeName(feat)})",
        SilhouetteEdge   => "SilhouetteEdge",
        null             => "null",
        _                => Microsoft.VisualBasic.Information.TypeName(entity),
    };

    private static Definition? RecoverTempAxisDefFromFaceAnchorLine(
        File file, Sketch sketch, Face2 face, SketchLine line) {
        var s = line.GetStartPoint2() as SketchPoint;
        var e = line.GetEndPoint2() as SketchPoint;
        if (s is null || e is null) return null;
        if (!TryCurvedFaceAxis(face, out var origin, out var direction)) return null;

        var start = SketchToModel(file, sketch, s.X, s.Y, s.Z);
        var end = SketchToModel(file, sketch, e.X, e.Y, e.Z);
        var lineDir = new[] {
            end[0] - start[0],
            end[1] - start[1],
            end[2] - start[2],
        };
        if (!VectorsParallel(lineDir, direction)) return null;

        var projectedStart = ProjectPointOntoAxis(start, origin, direction);
        var projectedEnd = ProjectPointOntoAxis(end, origin, direction);
        var (orderedStart, orderedEnd) = OrderPoints(projectedStart, projectedEnd);
        var def = new TempAxisDefinition(orderedStart, orderedEnd);
        SldworksLog.Information(
            "RecoverTempAxis: {SketchName} projected curved-face raw SketchLine onto face axis => {AxisDefinition}",
            OwningSketchName(sketch), def.ToJson().ToJsonString());
        return def;
    }

    private static bool TryCurvedFaceAxis(Face2 face, out Point3D origin, out double[] direction) {
        var surface = face.IGetSurface();
        if (surface is not null && surface.IsCylinder()) {
            var p = (double[])surface.CylinderParams;
            origin = new Point3D(p[0], p[1], p[2]);
            direction = new[] { p[3], p[4], p[5] };
            return true;
        }
        if (surface is not null && surface.IsCone()) {
            var p = (double[])surface.ConeParams2;
            origin = new Point3D(p[0], p[1], p[2]);
            direction = new[] { p[3], p[4], p[5] };
            return true;
        }
        if (surface is not null && surface.IsTorus()) {
            var p = (double[])surface.TorusParams;
            origin = new Point3D(p[0], p[1], p[2]);
            direction = new[] { p[3], p[4], p[5] };
            return true;
        }
        origin = new Point3D(0, 0, 0);
        direction = Array.Empty<double>();
        return false;
    }

    private static Point3D ProjectPointOntoAxis(double[] point, Point3D origin, double[] direction) {
        var len = Math.Sqrt(
            direction[0] * direction[0] +
            direction[1] * direction[1] +
            direction[2] * direction[2]);
        var dx = direction[0] / len;
        var dy = direction[1] / len;
        var dz = direction[2] / len;
        var vx = point[0] - origin.X;
        var vy = point[1] - origin.Y;
        var vz = point[2] - origin.Z;
        var t = vx * dx + vy * dy + vz * dz;
        return new Point3D(
            origin.X + t * dx,
            origin.Y + t * dy,
            origin.Z + t * dz);
    }

    private static bool VectorsParallel(double[] a, double[] b) {
        var aLen = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        var bLen = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
        if (aLen <= Flags.GeometryTolerance || bLen <= Flags.GeometryTolerance) return false;
        var dot = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (aLen * bLen);
        return Math.Abs(Math.Abs(dot) - 1.0) <= 1e-5;
    }

    private static (Point3D Start, Point3D End) OrderPoints(Point3D a, Point3D b) =>
        ComparePoint3D(a, b) <= 0 ? (a, b) : (b, a);

    private static int ComparePoint3D(Point3D a, Point3D b) {
        var c = a.X.CompareTo(b.X);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        if (c != 0) return c;
        return a.Z.CompareTo(b.Z);
    }

    // Dispatch wrapper: SW returns the implicit temp axis as either an internal
    // SketchLine (Revolve/CutRevolve features, cylindrical-face axis with extent)
    // or an internal SketchPoint (cylindrical-face axis through a single anchor).
    // Both expose sketch-space coords that must be projected to model space via
    // the sketch's ModelToSketchTransform inverse before SelectByID2("AXIS") can
    // pick the live temp axis.
    //
    // Returns the CAPTURED Definition (not a RefAxis): the SelectByID2-recovered
    // RefAxis proxy is bound to the current selection and dies on the
    // ClearSelection2 that has to happen before this function returns. The
    // recovery + Capture must run with the proxy still alive. For temp axes,
    // Capture grabs the owning Face2 via GetTempAxisReferenceFace, which IS a
    // durable body-face proxy — safe to return.
    private static Definition? RecoverTempAxisDefFromRawEntity(
        File file, Sketch sketch, object? raw, bool requireEndpointMatch) {
        var sketchName = OwningSketchName(sketch);
        SldworksLog.Information(
            "RecoverTempAxis: {SketchName} raw={RawType} requireEndpointMatch={RequireEndpointMatch}",
            sketchName, TypeLabel(raw), requireEndpointMatch);

        Definition? def;
        switch (raw) {
            case SketchLine line:
                SldworksLog.Information(
                    "RecoverTempAxis: {SketchName} raw matched SketchLine",
                    sketchName);
                def = RecoverTempAxisDefFromSketchLine(file, sketch, line, requireEndpointMatch);
                break;
            case SketchPoint point:
                var pick = SketchToModel(file, sketch, point.X, point.Y, point.Z);
                SldworksLog.Information(
                    "RecoverTempAxis: {SketchName} raw matched SketchPoint; pick=({X}, {Y}, {Z})",
                    sketchName, pick[0], pick[1], pick[2]);
                def = RecoverTempAxisDefAt(file, pick);
                break;
            default:
                SldworksLog.Information(
                    "RecoverTempAxis: {SketchName} raw did not match SketchLine/SketchPoint; returning null",
                    sketchName);
                return null;
        }

        SldworksLog.Information(
            "RecoverTempAxis: {SketchName} result={Result}",
            sketchName, def is null ? "null" : def.ToJson().ToJsonString());
        return def;
    }


    // SW quirk: SketchPoint / SketchLine endpoint X/Y/Z properties are in SKETCH
    // 2D space (z is usually 0). SelectByID2 and GetRefAxisParams work in MODEL
    // METERS, so we must project sketch → model via the sketch's transform
    // inverse before any model-space SW call.
    private static double[] SketchToModel(File file, Sketch sketch, double sx, double sy, double sz) {
        var inv = ((ISketch)sketch).ModelToSketchTransform?.IInverse();
        if (inv is null) return new[] { sx, sy, sz };
        var p = (MathPoint)file.MathUtil.CreatePoint(new[] { sx, sy, sz });
        var arr = (double[])p.IMultiplyTransform(inv).ArrayData;
        return new[] { arr[0], arr[1], arr.Length > 2 ? arr[2] : 0 };
    }

    // Recovers the implicit temp axis by sampling SelectByID2("AXIS") along the sketch line
    // (midpoint + 4 halving rounds = 15 points) and verifying via GetRefAxisParams against the
    // line endpoints. Captures the matched axis's Definition INLINE before ClearSelection,
    // because SelectByID2-recovered RefAxis proxies for temp axes die on clear.
    private static Definition? RecoverTempAxisDefFromSketchLine(
        File file, Sketch sketch, object? sketchLine, bool requireEndpointMatch) {
        if (sketchLine is not SketchLine line) return null;

        var s = line.GetStartPoint2() as SketchPoint;
        var e = line.GetEndPoint2() as SketchPoint;
        if (s is null || e is null) return null;

        // Project to MODEL meters — SelectByID2 + GetRefAxisParams both work in
        // model space, but SketchPoint.X/Y/Z is in sketch-2D (z=0) space.
        var startXyz = SketchToModel(file, sketch, s.X, s.Y, s.Z);
        var endXyz   = SketchToModel(file, sketch, e.X, e.Y, e.Z);
        SldworksLog.Information(
            "RecoverTempAxis: {SketchName} SketchLine model start=({SX}, {SY}, {SZ}) end=({EX}, {EY}, {EZ})",
            OwningSketchName(sketch),
            startXyz[0], startXyz[1], startXyz[2],
            endXyz[0], endXyz[1], endXyz[2]);

        var midPoint = new[] {
            (startXyz[0] + endXyz[0]) / 2,
            (startXyz[1] + endXyz[1]) / 2,
            (startXyz[2] + endXyz[2]) / 2,
        };
        // Initial offset = half the line vector; halved each round.
        var offset = new[] {
            (endXyz[0] - startXyz[0]) / 2,
            (endXyz[1] - startXyz[1]) / 2,
            (endXyz[2] - startXyz[2]) / 2,
        };

        var ext = file.ModelDoc.Extension;
        var selMgr = file.ModelDoc.ISelectionManager;
        // SW has no cheap "save selection" primitive; clear without restore (the surrounding ReadAll walk doesn't depend on prior selection).
        file.ModelDoc.ClearSelection2(true);

        var points = new List<double[]> { midPoint };
        var sampled = 0;
        var axisCandidates = 0;
        var endpointMismatches = 0;
        for (var round = 0; round < 5; round++) {
            foreach (var p in points) {
                sampled++;
                file.ModelDoc.ClearSelection2(true);
                ext.SelectByID2(
                    "", "AXIS", p[0], p[1], p[2],
                    /*append*/ false, /*mark*/ 0, /*callout*/ null,
                    (int)swSelectOption_e.swSelectOptionExtensive);
                var axis = UnwrapAxisFromSelection(selMgr);
                if (axis is null) continue;
                axisCandidates++;
                if (axis.GetRefAxisParams() is not double[] axisParams || axisParams.Length < 6) continue;
                if (requireEndpointMatch && !EndpointsMatch(axisParams, startXyz, endXyz)) {
                    endpointMismatches++;
                    continue;
                }

                // Capture WHILE the proxy is live, then clear.
                if (!requireEndpointMatch && !EndpointsMatch(axisParams, startXyz, endXyz)) {
                    SldworksLog.Information(
                        "RecoverTempAxis: {SketchName} accepting selected AXIS without endpoint match for curved-face anchor line",
                        OwningSketchName(sketch));
                }
                var def = DefinitionCapture.Capture(axis);
                file.ModelDoc.ClearSelection2(true);
                return def;
            }
            offset = new[] { offset[0] / 2, offset[1] / 2, offset[2] / 2 };
            var nextPoints = new List<double[]>(points.Count * 2);
            foreach (var p in points) {
                nextPoints.Add(new[] { p[0] + offset[0], p[1] + offset[1], p[2] + offset[2] });
                nextPoints.Add(new[] { p[0] - offset[0], p[1] - offset[1], p[2] - offset[2] });
            }
            points = nextPoints;
        }
        file.ModelDoc.ClearSelection2(true);
        SldworksLog.Information(
            "RecoverTempAxis: {SketchName} SketchLine failed; sampled={Sampled}, axisCandidates={AxisCandidates}, endpointMismatches={EndpointMismatches}, requireEndpointMatch={RequireEndpointMatch}",
            OwningSketchName(sketch), sampled, axisCandidates, endpointMismatches, requireEndpointMatch);
        return null;
    }

    // Single-pick variant: SelectByID2("AXIS") at one model-space point. Used
    // when the implicit temp axis is anchored at a SketchPoint (no extent to
    // sample along, unlike the SketchLine case). Captures Definition INLINE
    // before ClearSelection (proxy dies on clear).
    private static Definition? RecoverTempAxisDefAt(File file, double[] pick) {
        var ext = file.ModelDoc.Extension;
        var selMgr = file.ModelDoc.ISelectionManager;
        file.ModelDoc.ClearSelection2(true);
        ext.SelectByID2(
            "", "AXIS", pick[0], pick[1], pick[2],
            /*append*/ false, /*mark*/ 0, /*callout*/ null,
            (int)swSelectOption_e.swSelectOptionExtensive);
        var selected = selMgr.GetSelectedObject6(1, 0);
        var axis = UnwrapAxisFromSelection(selMgr);
        Definition? def = axis is null ? null : DefinitionCapture.Capture(axis);
        SldworksLog.Information(
            "RecoverTempAxis: point pick=({X}, {Y}, {Z}) selected={SelectedType} result={Result}",
            pick[0], pick[1], pick[2], TypeLabel(selected),
            def is null ? "null" : def.ToJson().ToJsonString());
        file.ModelDoc.ClearSelection2(true);
        return def;
    }

    // SW 2026 returns the picked axis either as a RefAxis directly or as a
    // Feature wrapping it (synthesized for the selection); unwrap both. The
    // returned proxy is bound to the current selection — caller must extract
    // everything from it BEFORE ClearSelection2.
    private static RefAxis? UnwrapAxisFromSelection(SelectionMgr selMgr) {
        var sel = selMgr.GetSelectedObject6(1, 0);
        return sel switch {
            RefAxis ra => ra,
            Feature f  => f.GetSpecificFeature2() as RefAxis,
            _          => null,
        };
    }

    private static bool EndpointsMatch(double[] axisParams, double[] start, double[] end) {
        // SW quirk: GetRefAxisParams = [startXYZ, endXYZ] in METERS; axis is undirected, so accept either orientation.
        var aStart = new[] { axisParams[0], axisParams[1], axisParams[2] };
        var aEnd   = new[] { axisParams[3], axisParams[4], axisParams[5] };
        return (PointsClose(aStart, start) && PointsClose(aEnd, end))
            || (PointsClose(aStart, end)   && PointsClose(aEnd, start));
    }

    private static bool PointsClose(double[] a, double[] b) {
        const double tol = Flags.GeometryTolerance;
        return Math.Abs(a[0] - b[0]) <= tol
            && Math.Abs(a[1] - b[1]) <= tol
            && Math.Abs(a[2] - b[2]) <= tol;
    }

    // True iff the SW-proxy entity's owning sketch matches the sketch we're capturing
    // constraints for. Cross-sketch refs are passed through unchecked — they resolve
    // on target via their own sketch_name.
    private static bool RefBelongsToSameSketch(object entity, string owningSketchName) {
        var ownerSketch = entity switch {
            ISketchSegment seg => seg.GetSketch(),
            ISketchPoint pt    => pt.GetSketch(),
            _                  => null,
        };
        if (ownerSketch is not Feature feat) return false;
        return feat.Name == owningSketchName;
    }

    // Best-effort key extraction. Returns null when SketchEntityIdFor can't classify
    // the runtime type (caller already gated on SketchSegment / SketchPoint above, so
    // this null path is defensive).
    private static (string Kind, long Id)? TryRefKey(object entity, string sketchName) {
        try {
            var sid = DefinitionCapture.SketchEntityIdFor(entity, sketchName);
            return (sid.EntityKind, sid.Id);
        } catch (InvalidOperationException) {
            return null;
        }
    }

    private static string OwningSketchName(Sketch sketch) =>
        sketch is Feature feat ? (feat.Name ?? "") : "";

    // True iff `face`'s surface has an implied temporary axis (cylinder, cone, torus).
    // Gates the temp-axis recovery path so planar faces still capture as planar_face.
    private static bool IsCylindricalOrConical(Face2 face) {
        var surface = face.IGetSurface();
        if (surface is null) return false;
        return surface.IsCylinder() || surface.IsCone() || surface.IsTorus();
    }
}
