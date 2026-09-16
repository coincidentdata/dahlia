using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Sketches;
using Sldworks.Core.Handlers.Sketches.Composites;
using Sldworks.Core.Handlers.Sketches.Entities;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using PointEntity = Sldworks.Core.Handlers.Sketches.Entities.Point;

namespace Sldworks.Core.Handlers;

// Sketch feature orchestrator. Enters the sketch and dispatches per-entity Parse on
// Inspect / Add on Add. SketchConstraint.ReadAll runs on Inspect; constraints are NOT
// part of Add — the caller invokes AddConstraints (a separate plugin verb) once the
// freshly-Added sketch has its target-side ids back-filled into the model. That split
// (a) lets constraints be added to a pre-existing or empty sketch and (b) avoids the
// per-Add source→target id map: refs go straight through DefinitionResolver.
public static class SketchHandler {
    public const string TypeName = "Sketch";
    // SW quirk: feature.GetTypeName2() returns "ProfileFeature" for a sketch, NOT "Sketch".
    public const string SwTypeName = "ProfileFeature";

    public static JsonNode Inspect(File file, Feature feature) {
        var sketchObj = feature.GetSpecificFeature2() as Sketch
            ?? throw new InvalidOperationException(
                $"SketchHandler.Inspect: feature '{feature.Name}' GetSpecificFeature2 did not return a Sketch");

        // SW quirk: sketch.GetReferenceEntity REQUIRES the sketch to be entered to return
        // the plane reference; pulling sketch entities also requires edit mode for the
        // ref proxy to surface owning-sketch correctly.
        EnterSketchForInspect(file, feature);
        try {
            var planeJson = CapturePlane(sketchObj);

            var sketchName = feature.Name ?? "";
            var (entitiesArr, emittedIds) = ReadEntities(sketchObj, sketchName);

            // SW quirk A11: temp-axes preference must be on while reading constraints, otherwise
            // implicit Revolve/Cut-Revolve axis proxies don't surface on relation entity lists.
            // Wireframe view is also required — SelectByID2("AXIS") at a point inside a solid
            // body silently misses the underlying temp axis when the body's shaded faces
            // occlude the pick. Both toggles match what AddConstraints sets on the Add side.
            JsonArray constraintsArr;
            var ext = file.ModelDoc.Extension;
            var oldAxes = ext.GetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            var activeView = file.ActiveView;
            var oldDisplay = activeView.DisplayMode;
            ext.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, true);
            activeView.DisplayMode = (int)swViewDisplayMode_e.swViewDisplayMode_Wireframe;
            try {
                constraintsArr = SketchConstraint.ReadAll(file, sketchObj, emittedIds);
            } finally {
                ext.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
                    (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, oldAxes);
                activeView.DisplayMode = oldDisplay;
            }

            // Composite reconstruction. SketchConstraint.Read surfaces SW's structural
            // Patterned relations so per-composite parsers can fold the expanded
            // primitives + relations back into a single composite entity in the
            // entities array. Order matters:
            //
            //   1. PolygonParser  — closed Patterned rings + tangent-to-circle, strict.
            //   2. PatternParser  — unified linear+circular sketch patterns.
            //
            // Polygon runs first so a regular polygon's Patterned ring isn't
            // misclassified as a circular pattern (the ring's instance anchors do
            // sit on a circle). PatternParser then walks per-seed in source order,
            // greedily peeling off the longer of (linear, circular) fits — see
            // PatternParser.cs for the why. After both run, any Patterned constraints
            // that didn't fold into a composite get stripped — they're internal SW
            // relations with no public meaning.
            PolygonParser.Detect(entitiesArr, constraintsArr);
            PatternParser.Detect(entitiesArr, constraintsArr);
            StripLeftoverPatternedConstraints(constraintsArr);

            var axes = MathUtils.GetTransformMatrix(((ISketch)sketchObj).ModelToSketchTransform);
            var axesJson = new JsonArray();
            foreach (var row in axes) {
                axesJson.Add(new JsonArray(row[0], row[1], row[2]));
            }

            return new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["plane"] = planeJson,
                ["entities"] = entitiesArr,
                ["constraints"] = constraintsArr,
                ["echo_computed_axes"] = axesJson,
            };
        } finally {
            ExitSketch(file);
        }
    }

    public static JsonNode Add(File file, JsonNode feature) {
        // Constraints are added via the separate AddConstraints verb after the caller
        // has back-filled target-side ids onto the model's entities. Reject any
        // constraints embedded in the Add payload to surface mistakes early instead
        // of silently dropping them.
        if (feature["constraints"] is JsonArray suppliedConstraints && suppliedConstraints.Count > 0) {
            throw new ArgumentException(
                $"SketchHandler.Add: 'constraints' must be empty (got {suppliedConstraints.Count}); " +
                "use the AddConstraints verb after Add to apply constraints");
        }

        var planeNode = feature["plane"]
            ?? throw new ArgumentException("SketchHandler.Add: missing required field 'plane'");
        var planeDef = Definition.FromJson(planeNode)
            ?? throw new ArgumentException("SketchHandler.Add: 'plane' is not a valid Definition");
        var planeLive = DefinitionResolver.Resolve(file, planeDef)
            ?? throw new InvalidOperationException(
                $"SketchHandler.Add: plane Definition ({planeDef.GetType().Name}) did not resolve to a live entity");

        // SW quirk: selection mark 0 is the slot SketchManager.InsertSketch reads for the plane.
        file.ModelDoc.ClearSelection2(true);
        SelectPlane(file, planeLive);

        var sketchManager = file.ModelDoc.SketchManager;
        // SW quirk A8: InsertSketch(true) is a toggle — first call enters, second exits.
        sketchManager.InsertSketch(true);
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                "SketchHandler.Add: SketchManager.ActiveSketch is null after InsertSketch(true)");
        var sketchFeature = (Feature)sketch;

        var entitiesArr = feature["entities"] as JsonArray ?? new JsonArray();

        // SketchHandler.Add is phase 1: primitives + self-contained composites
        // (Polygon, Rectangle). Phase-2 composites that reference OTHER sketch
        // entities by id (LinearPattern, CircularPattern — their `seeds` resolve
        // through DefinitionResolver against the active sketch) MUST come in via
        // the separate AddSketchEntities verb so their seed ids can be back-filled
        // to target-side after this call returns. Reject them here to surface the
        // mistake instead of failing later inside DefinitionResolver.
        for (var i = 0; i < entitiesArr.Count; i++) {
            var typeStr = (entitiesArr[i] as JsonObject)?["type"]?.GetValue<string>();
            if (IsPhaseTwoCompositeType(typeStr)) {
                throw new ArgumentException(
                    $"SketchHandler.Add: entities[{i}] type '{typeStr}' references other " +
                    "sketch entities by id and must be added via AddSketchEntities after " +
                    "the seed entities' target-side ids are back-filled.");
            }
        }

        // One ISketchEntity per input entity, in input order. Each entity stores its
        // own live SW reference(s) at Add time and captures itself as a JSON entry
        // via GetJson at end-of-flow, so embedded ids and coords reflect the settled
        // state of the sketch.
        var entities = new List<ISketchEntity>(entitiesArr.Count);

        // SW quirk: AddToDB=true is required during scripted entity add to suppress
        // snap/inference; without it, neighboring geometry inadvertently coincident-snaps.
        sketchManager.AddToDB = true;
        try {
            for (var i = 0; i < entitiesArr.Count; i++) {
                var entityNode = entitiesArr[i]
                    ?? throw new ArgumentException($"SketchHandler.Add: entities[{i}] is null");
                entities.Add(AddOneEntity(file, sketch, sketchManager, entityNode, i));
            }
            // SW quirk: Sketch::MergePoints requires "only one open contour" per the SW API
            // remarks, which is never the case for our sketches; observed behavior in 2026
            // is a silent no-op even for standalone coincident points. Coincident-by-
            // construction endpoints are left to SW's solver to fuse on its own.
        } finally {
            sketchManager.AddToDB = false;
        }

        // SW quirk A8: second InsertSketch(true) exits.
        sketchManager.InsertSketch(true);

        // Hide the sketch to reduce feature-tree / graphics clutter for the user. Blanked via
        // IModelDoc2.BlankSketch (select feature, blank, clear). CRITICAL: BlankSketch is
        // DISPLAY-ONLY — a blanked sketch's entities remain programmatically selectable by id
        // (SelectByID2 / per-sketch ids), so later features (extrude/revolve/sweep profiles, the
        // helix axis sketch, projected curves) can still reference this sketch. This is NOT
        // suppress/delete/rollback. Inspect below re-enters edit mode regardless of blank state,
        // so geometry round-trip is unaffected. Cosmetic — a hide failure must not abort a
        // successfully-created sketch, so swallow and warn rather than throw.
        try {
            sketchFeature.Select2(false, 0);
            file.ModelDoc.BlankSketch();
            file.ModelDoc.ClearSelection2(true);
        } catch (Exception ex) {
            SldworksLog.Warning(
                "SketchHandler.Add: failed to hide sketch {Name} — leaving it visible: {Error}",
                sketchFeature.Name, ex.Message);
        }

        SldworksLog.Information("SketchHandler.Add: created {Name}", sketchFeature.Name);

        // Return Inspect output so the caller sees back-filled (id, name) on every entity. The
        // sketchFeature COM proxy is still valid post-exit; Inspect re-enters internally.
        // Then overlay `entities` with each input entity's own GetJson capture — preserves
        // the caller's authoring order and keeps composites whole (bulk Inspect walks SW's
        // enumeration order and would emit a composite as its expanded primitives).
        // Honor source name so subsequent features can resolve the sketch by SketchEntityId.SketchName.
        File.ApplyFeatureName(sketchFeature, feature);
        var inspectResult = (JsonObject)Inspect(file, sketchFeature);
        var entitiesOut = new JsonArray();
        foreach (var entity in entities) {
            entitiesOut.Add(entity.GetJson(sketchFeature.Name));
        }
        inspectResult["entities"] = entitiesOut;
        return inspectResult;
    }

    // Phase-2 composite types — references-other-entities-by-id composites that must
    // be added AFTER their seeds' target-side ids are back-filled into the wire payload.
    // Currently: LinearPattern (pattern.seeds), CircularPattern (pattern.seeds). Future
    // additions (OffsetEntity, etc.) extend this list.
    private static bool IsPhaseTwoCompositeType(string? typeStr) =>
        typeStr == SketchLinearPattern.TypeName
        || typeStr == SketchCircularPattern.TypeName;

    // Phase-2 entity dispatch: takes phase-2 composites (LinearPattern / CircularPattern
    // / future OffsetEntity) for an existing sketch and adds them to the live SW
    // sketch, capturing their generated instance entities. The seeds in each composite
    // must already carry target-side SketchEntityIds — the caller back-filled these
    // from the AddSketch response before serializing this payload.
    //
    // Returns the freshly-added composite entities as `entities` so the caller can
    // back-fill the new instance ids into its in-memory model. Constraints aren't
    // added here — that's still AddConstraints' job.
    public static JsonNode AddSketchEntities(File file, string sketchName, JsonArray entitiesArr) {
        // Defense-in-depth: every entity must be a phase-2 composite. Primitives
        // and self-contained composites belong to AddSketch and we'd be unable
        // to attach them to a sketch already exited from edit mode the same way
        // anyway; cleaner to reject the misuse early.
        for (var i = 0; i < entitiesArr.Count; i++) {
            var typeStr = (entitiesArr[i] as JsonObject)?["type"]?.GetValue<string>();
            if (!IsPhaseTwoCompositeType(typeStr)) {
                throw new ArgumentException(
                    $"SketchHandler.AddSketchEntities: entities[{i}] type '{typeStr}' is not a " +
                    "phase-2 composite (LinearPattern / CircularPattern). Use AddSketch for " +
                    "primitives + self-contained composites (Polygon).");
            }
        }

        var sketchFeature = FindSketchFeatureByName(file, sketchName)
            ?? throw new ArgumentException(
                $"SketchHandler.AddSketchEntities: no sketch feature named '{sketchName}' in the active model");

        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                $"SketchHandler.AddSketchEntities: failed to enter sketch '{sketchName}' (ActiveSketch null after EditSketch)");

        var entities = new List<ISketchEntity>(entitiesArr.Count);
        // SW quirk: AddToDB=true suppresses snap/inference during scripted entity
        // add — the same envelope SketchHandler.Add uses for primitives.
        sketchManager.AddToDB = true;
        try {
            for (var i = 0; i < entitiesArr.Count; i++) {
                var entityNode = entitiesArr[i]
                    ?? throw new ArgumentException($"SketchHandler.AddSketchEntities: entities[{i}] is null");
                var typeStr = (entityNode as JsonObject)?["type"]?.GetValue<string>()
                    ?? throw new ArgumentException(
                        $"SketchHandler.AddSketchEntities: entities[{i}] missing 'type' discriminator");
                entities.Add(AddOneComposite(file, sketch, sketchManager, entityNode, i, typeStr));
            }
            // No MergePoints — see the matching note in SketchHandler.Add about why
            // Sketch::MergePoints is not used.
        } finally {
            sketchManager.AddToDB = false;
        }

        sketchManager.InsertSketch(true);

        SldworksLog.Information(
            "SketchHandler.AddSketchEntities: added {Count} composite entities to {Name}",
            entitiesArr.Count, sketchFeature.Name);

        var entitiesOut = new JsonArray();
        foreach (var entity in entities) {
            entitiesOut.Add(entity.GetJson(sketchFeature.Name));
        }
        return new JsonObject {
            ["name"] = sketchFeature.Name,
            ["entities"] = entitiesOut,
        };
    }

    // Apply a list of constraints to an existing sketch identified by feature name.
    // Refs in each constraint must already carry target-side SketchEntityIds (the
    // caller back-fills these from the AddSketch response); resolution goes straight
    // through DefinitionResolver. Re-enters edit mode under the temp-axes/wireframe
    // envelope, applies each constraint, and exits.
    public static JsonNode AddConstraints(File file, string sketchName, JsonArray constraintsArr) {
        var sketchFeature = FindSketchFeatureByName(file, sketchName)
            ?? throw new ArgumentException(
                $"SketchHandler.AddConstraints: no sketch feature named '{sketchName}' in the active model");

        // SW quirk A9 (arcs bug): exit any active sketch BEFORE entering this one.
        // The constraints phase always starts from a clean exit + enter.
        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                $"SketchHandler.AddConstraints: failed to enter sketch '{sketchName}' (ActiveSketch null after EditSketch)");

        // SW quirk A11: temp-axes ON + wireframe display while adding constraints.
        var ext = file.ModelDoc.Extension;
        var oldAxes = ext.GetUserPreferenceToggle(
            (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
            (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
        var activeView = file.ActiveView;
        var oldDisplay = activeView.DisplayMode;
        ext.SetUserPreferenceToggle(
            (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
            (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, true);
        activeView.DisplayMode = (int)swViewDisplayMode_e.swViewDisplayMode_Wireframe;
        try {
            for (var i = 0; i < constraintsArr.Count; i++) {
                var constraintNode = constraintsArr[i]
                    ?? throw new ArgumentException($"SketchHandler.AddConstraints: constraints[{i}] is null");
                SketchConstraint.Add(file, sketch, constraintNode);
            }
        } finally {
            ext.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swDisplayTemporaryAxes,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, oldAxes);
            activeView.DisplayMode = oldDisplay;
        }

        // Exit edit mode.
        sketchManager.InsertSketch(true);

        SldworksLog.Information(
            "SketchHandler.AddConstraints: applied {Count} constraints to {Name}",
            constraintsArr.Count, sketchFeature.Name);

        return new JsonObject {
            ["name"] = sketchFeature.Name,
            ["count"] = constraintsArr.Count,
        };
    }

    // Edit ONE existing primitive sketch entity (Line / Circle / Arc / Point) IN PLACE by
    // moving its defining points (and radius). The entity's id is preserved — we mutate the
    // live segment, never delete + recreate — so constraints that reference it survive.
    // `entityJson` is a single entity from Inspect's `entities` array with modified geometry.
    //
    // Composites (Polygon, LinearPattern, CircularPattern, Rectangle) are NOT
    // SketchEntityDefinition subclasses — they have no single (kind, id) handle and are
    // rejected; edit their member primitives individually instead. Best on undimensioned
    // geometry: on a fully-constrained DOF the solver overrides a raw coord change.
    public static JsonNode EditSketchEntity(File file, string sketchName, JsonNode entityJson) {
        var def = Definition.FromJson(entityJson)
            ?? throw new ArgumentException(
                "EditSketchEntity: entity payload did not deserialize to a Definition");
        if (def is not SketchEntityDefinition entity) {
            throw new ArgumentException(
                $"EditSketchEntity: '{def.GetType().Name}' is not a single primitive sketch entity — "
                + "composites (Polygon / LinearPattern / CircularPattern) have no single id and can't "
                + "be edited as a unit; edit their member Line/Arc/Circle primitives individually.");
        }

        var sketchFeature = FindSketchFeatureByName(file, sketchName)
            ?? throw new ArgumentException(
                $"EditSketchEntity: no sketch feature named '{sketchName}' in the active model");

        // Re-enter the sketch (mirrors AddConstraints): exit any active sketch first.
        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                $"EditSketchEntity: failed to enter sketch '{sketchName}' (ActiveSketch null after EditSketch)");

        try {
            var live = DefinitionResolver.Resolve(file, entity, sketch)
                ?? throw new InvalidOperationException(
                    $"EditSketchEntity: {entity.Id.EntityKind}#{entity.Id.Id} did not resolve to a live "
                    + $"entity in sketch '{sketchName}'");
            ApplyEntityGeometry(entity, live);
        } finally {
            sketchManager.InsertSketch(true);   // exit edit mode (toggle)
        }

        // Roll to end so the moved geometry re-solves + propagates downstream. NOT
        // AfterFeature: a mid-tree sketch must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "SketchHandler.EditSketchEntity: moved {Kind}#{Id} in {Name}",
            entity.Id.EntityKind, entity.Id.Id, sketchName);

        return Inspect(file, sketchFeature);
    }

    // Apply the parsed entity's geometry onto the live segment by moving its defining
    // SketchPoints (and SetRadius for circles). Per-kind; unsupported kinds throw.
    private static void ApplyEntityGeometry(SketchEntityDefinition entity, object live) {
        switch (entity) {
            case Line ln: {
                var seg = live as SketchLine
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchLine");
                MovePoint(seg.IGetStartPoint2(), ln.Start, "Line.start");
                MovePoint(seg.IGetEndPoint2(), ln.End, "Line.end");
                break;
            }
            case Circle c: {
                var arc = live as SketchArc
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchArc (Circle)");
                MovePoint(arc.IGetCenterPoint2(), c.Center, "Circle.center");
                arc.SetRadius(c.Radius);
                break;
            }
            case Arc a: {
                var arc = live as SketchArc
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchArc");
                // Center + both endpoints; radius follows from |center→start|. The caller must
                // keep |center→start| == |center→end| or SW snaps end to the center-start radius.
                MovePoint(arc.IGetCenterPoint2(), a.Center, "Arc.center");
                MovePoint(arc.IGetStartPoint2(), a.Start, "Arc.start");
                MovePoint(arc.IGetEndPoint2(), a.End, "Arc.end");
                break;
            }
            case PointEntity p: {
                var pt = live as SketchPoint
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchPoint");
                pt.SetCoords(p.P.X, p.P.Y, 0.0);
                break;
            }
            case Ellipse e: {
                var ell = live as SketchEllipse
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchEllipse");
                MovePoint(ell.IGetCenterPoint2(), e.Center, "Ellipse.center");
                MovePoint(ell.IGetMajorPoint2(), e.MajorAxisEnd, "Ellipse.major_axis_end");
                MovePoint(ell.IGetMinorPoint2(), e.MinorAxisEnd, "Ellipse.minor_axis_end");
                break;
            }
            case EllipticalArc ea: {
                var ell = live as SketchEllipse
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchEllipse (EllipticalArc)");
                // Direction (clockwise) is structural — set at creation, not movable here; only the
                // five defining points are repositioned. Keep |center→major|, |center→minor| sane.
                MovePoint(ell.IGetCenterPoint2(), ea.Center, "EllipticalArc.center");
                MovePoint(ell.IGetMajorPoint2(), ea.MajorAxisEnd, "EllipticalArc.major_axis_end");
                MovePoint(ell.IGetMinorPoint2(), ea.MinorAxisEnd, "EllipticalArc.minor_axis_end");
                MovePoint(ell.IGetStartPoint2(), ea.Start, "EllipticalArc.start");
                MovePoint(ell.IGetEndPoint2(), ea.End, "EllipticalArc.end");
                break;
            }
            case Parabola pb: {
                var par = live as SketchParabola
                    ?? throw new InvalidOperationException(
                        $"EditSketchEntity: resolved {live.GetType().Name}, expected SketchParabola");
                // SW quirk: SketchParabola re-fits to its four points, so a single SetCoords leaves
                // residual drift — two passes, mirroring Parabola.Add.
                var focalPt = par.IGetFocalPoint2();
                var apexPt  = par.IGetApexPoint2();
                var startPt = par.IGetStartPoint2();
                var endPt   = par.IGetEndPoint2();
                for (var i = 0; i < 2; i++) {
                    focalPt?.SetCoords(pb.Focal.P.X, pb.Focal.P.Y, 0.0);
                    apexPt?.SetCoords(pb.Apex.P.X, pb.Apex.P.Y, 0.0);
                    startPt?.SetCoords(pb.Start.P.X, pb.Start.P.Y, 0.0);
                    endPt?.SetCoords(pb.End.P.X, pb.End.P.Y, 0.0);
                }
                break;
            }
            case Spline:
                // EXCLUDED — no safe in-place spline edit. SW's ModifyControlPoint / ModifyKnot
                // are unreliable across spline representations: on a generic (ConvertToModif'd)
                // spline the Modify* calls CRASH the SW process (RPC dies); on a plain B-spline
                // GetControlVertexWeights returns null so the control-point count can't even be
                // validated. Same call as ProjectedCurve's feature-edit exclusion: when SW's
                // in-place API crashes, exclude rather than risk it. Reshape a spline by
                // delete_entity + re-add (loses the id, so re-author dependent constraints).
                throw new ArgumentException(
                    "EditSketchEntity: in-place editing of a Spline is not supported — SW's "
                    + "ModifyControlPoint/ModifyKnot crash the process on some splines and return no "
                    + "control-vertex count on others. Delete the spline and re-add it with the new "
                    + "control points instead.");
            default:
                throw new ArgumentException(
                    $"EditSketchEntity: editing '{entity.GetType().Name}' is not supported — "
                    + "supported: Line, Circle, Arc, Point, Ellipse, EllipticalArc, Parabola "
                    + "(Spline excluded — its SW edit API is unstable).");
        }
    }

    private static void MovePoint(SketchPoint? livePoint, PointEntity to, string what) {
        if (livePoint is null) {
            throw new InvalidOperationException($"EditSketchEntity: live {what} point was null");
        }
        livePoint.SetCoords(to.P.X, to.P.Y, 0.0);
    }

    // Delete ONE existing primitive sketch entity (Line/Arc/Circle/Point/Ellipse/Spline/...).
    // `entityJson` is a single entity from Inspect's `entities` array — only its id is used.
    // Deleting the entity drops its (unshared) child points and any relations referencing it;
    // a downstream feature that consumed it will then fail on rebuild (that's the caller's
    // intent when deleting). Composites have no single id and are rejected — delete member
    // primitives individually.
    public static JsonNode DeleteSketchEntity(File file, string sketchName, JsonNode entityJson) {
        var def = Definition.FromJson(entityJson)
            ?? throw new ArgumentException(
                "DeleteSketchEntity: entity payload did not deserialize to a Definition");
        if (def is not SketchEntityDefinition entity) {
            throw new ArgumentException(
                $"DeleteSketchEntity: '{def.GetType().Name}' is not a single primitive sketch entity — "
                + "composites (Polygon / LinearPattern / CircularPattern) have no single id; "
                + "delete their member Line/Arc/Circle primitives individually.");
        }

        var sketchFeature = FindSketchFeatureByName(file, sketchName)
            ?? throw new ArgumentException(
                $"DeleteSketchEntity: no sketch feature named '{sketchName}' in the active model");

        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                $"DeleteSketchEntity: failed to enter sketch '{sketchName}' (ActiveSketch null after EditSketch)");

        bool deleted;
        try {
            var live = DefinitionResolver.Resolve(file, entity, sketch)
                ?? throw new InvalidOperationException(
                    $"DeleteSketchEntity: {entity.Id.EntityKind}#{entity.Id.Id} did not resolve to a live "
                    + $"entity in sketch '{sketchName}'");
            file.ModelDoc.ClearSelection2(true);
            if (!Definition.SelectLive(file, live, 0)) {
                throw new InvalidOperationException(
                    $"DeleteSketchEntity: could not select {entity.Id.EntityKind}#{entity.Id.Id} for deletion");
            }
            // Option 0 matches File.DeleteFeature; SW removes the entity's own relations.
            deleted = file.ModelDoc.Extension.DeleteSelection2(0);
        } finally {
            sketchManager.InsertSketch(true);   // exit edit mode (toggle)
        }
        if (!deleted) {
            throw new InvalidOperationException(
                $"DeleteSketchEntity: DeleteSelection2 returned false for {entity.Id.EntityKind}#{entity.Id.Id}");
        }

        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "SketchHandler.DeleteSketchEntity: deleted {Kind}#{Id} from {Name}",
            entity.Id.EntityKind, entity.Id.Id, sketchName);

        return Inspect(file, sketchFeature);
    }

    // Edit a sketch DRIVING DIMENSION in place by name — change its value and/or rename it.
    // The dimension is addressed by its SW name (the `echo_dim_name` Inspect exposes on each
    // dimension constraint, e.g. "D1@Sketch1"), which is globally unique, so no sketch re-entry
    // is needed to find it: ModelDoc.IParameter resolves it model-level. Renaming sets the
    // dimension's SHORT name (the part before '@'); its FullName then becomes "<newName>@<owner>".
    // Returns the owning sketch's Inspect so the caller sees the recomputed value + new name.
    public static JsonNode EditDimension(File file, string dimName, double? value, string? newName) {
        if (value is null && string.IsNullOrEmpty(newName)) {
            throw new ArgumentException(
                "EditDimension: nothing to change — provide a value and/or a new_name");
        }
        var dim = file.ModelDoc.IParameter(dimName)
            ?? throw new ArgumentException(
                $"EditDimension: no driving dimension named '{dimName}' in the model "
                + "(pass the echo_dim_name from inspect, e.g. \"D1@Sketch1\")");

        if (value.HasValue) {
            var rc = dim.SetSystemValue3(
                value.Value,
                (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,
                null);
            if (rc != (int)swSetValueReturnStatus_e.swSetValue_Successful) {
                throw new InvalidOperationException(
                    $"EditDimension: SetSystemValue3({value.Value:G6}) on '{dimName}' failed with "
                    + $"status {(swSetValueReturnStatus_e)rc} (e.g. driven dimension or invalid value)");
            }
        }
        if (!string.IsNullOrEmpty(newName)) {
            // SW quirk: IDimension.Name is the SHORT name (no '@owner'); FullName updates to match.
            dim.Name = newName;
        }

        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "SketchHandler.EditDimension: {DimName} -> value={Value} name={NewName}",
            dimName, value, newName);

        // Return the owning sketch's Inspect when the owner is a sketch (the primary use case).
        // Owner is the part after the last '@' in the ORIGINAL name — rename only changes the
        // pre-'@' part, so this stays valid. Fall back to a compact result for non-sketch owners.
        var ownerName = OwnerFromDimName(dimName);
        if (ownerName is not null && FindSketchFeatureByName(file, ownerName) is Feature sketchFeature) {
            return Inspect(file, sketchFeature);
        }
        return new JsonObject {
            ["name"] = dim.FullName,
            ["value"] = DimensionConstraint.ReadValue(dim),
        };
    }

    // Delete ONE existing constraint (a driving dimension or a geometric relation) from a sketch.
    // `constraintJson` is a single entry from Inspect's `constraints` array. Dimensions are
    // matched by their unique name (`echo_dim_name`); geometric relations are matched by kind +
    // the multiset of refs they touch — sketch entities by stable id, body topology (face/edge/
    // vertex) by resolving each ref to a live entity and comparing same-session captures (the
    // fuzzy part lives in DefinitionResolver; the comparison itself is exact). Deleting a
    // dimension frees the DOF it drove; deleting a relation frees the coupling — re-solves on rebuild.
    //
    // Limits (throw with a clear message rather than guess): an angle dimension that Inspect
    // surfaced as `Parallel` (value~0, no echo_dim_name) is backed by a DisplayDimension and
    // won't match the relation path; and a body ref captured as a TEMP AXIS on Inspect (curved
    // face standing in for an axis) re-captures as the raw face here and won't match. Both niche.
    public static JsonNode DeleteConstraint(File file, string sketchName, JsonNode constraintJson) {
        var wireKind = constraintJson["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("DeleteConstraint: constraint payload missing 'kind'");
        var wireDimName = constraintJson["echo_dim_name"]?.GetValue<string>();
        var refsArr = constraintJson["refs"] as JsonArray;

        var sketchFeature = FindSketchFeatureByName(file, sketchName)
            ?? throw new ArgumentException(
                $"DeleteConstraint: no sketch feature named '{sketchName}' in the active model");

        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        var sketch = sketchManager.ActiveSketch
            ?? throw new InvalidOperationException(
                $"DeleteConstraint: failed to enter sketch '{sketchName}' (ActiveSketch null after EditSketch)");

        bool deleted;
        try {
            // One comparable key per ref (sketch entity by id, body topology by a resolved +
            // re-captured key) — only for the relation path; dimensions match by name. Computed
            // inside sketch-edit so body refs resolve/capture in the same context as the relations.
            var wireKeys = wireDimName is null ? RefKeysFromWire(file, sketch, refsArr) : new List<string>();
            if (wireDimName is null && wireKeys.Count == 0) {
                throw new InvalidOperationException(
                    $"DeleteConstraint: constraint (kind '{wireKind}') has no identifiable refs to "
                    + "match on (neither sketch entities nor resolvable body topology).");
            }

            var manager = sketch.RelationManager
                ?? throw new InvalidOperationException(
                    $"DeleteConstraint: sketch '{sketchName}' has no RelationManager");
            var relations = manager.GetRelations((int)swSketchRelationFilterType_e.swAll) as object[]
                ?? Array.Empty<object>();

            var matches = new List<SketchRelation>();
            foreach (SketchRelation relation in relations) {
                if (relation.Suppressed) continue;
                if (relation.GetEntitiesCount() <= 0) continue;
                if (RelationMatches(relation, sketchName, wireKind, wireDimName, wireKeys)) {
                    matches.Add(relation);
                }
            }

            if (matches.Count == 0) {
                // Count same-kind relations so a near-miss reads as "N of this kind, none with
                // matching refs" rather than an opaque "no match".
                var sameKind = wireDimName is not null ? 0 : relations.Cast<SketchRelation>().Count(r =>
                    !r.Suppressed && r.GetEntitiesCount() > 0 && r.GetDisplayDimension() is null
                    && RelationConstraint.KindNameForTypeInt(r.GetRelationType()) == wireKind);
                throw new InvalidOperationException(
                    $"DeleteConstraint: no constraint in sketch '{sketchName}' matched "
                    + (wireDimName is not null
                        ? $"dimension name '{wireDimName}'"
                        : $"kind '{wireKind}' with refs {DescribeKeys(wireKeys)} "
                          + $"({sameKind} relation(s) of this kind exist but none with matching refs)"));
            }
            if (matches.Count > 1) {
                throw new InvalidOperationException(
                    $"DeleteConstraint: {matches.Count} constraints in sketch '{sketchName}' matched "
                    + $"kind '{wireKind}' / refs {DescribeKeys(wireKeys)} — ambiguous; cannot pick one to delete");
            }
            // SW quirk: RelationManager.DeleteRelation removes a GEOMETRIC relation but returns
            // false (no-op) for the relation backing a dimension — a dimension is deleted through
            // its DisplayDimension's annotation (select + DeleteSelection2), not the relation.
            if (wireDimName is not null) {
                var disp = matches[0].GetDisplayDimension() as DisplayDimension;
                var ann = disp?.GetAnnotation() as Annotation
                    ?? throw new InvalidOperationException(
                        $"DeleteConstraint: dimension '{wireDimName}' has no annotation to delete");
                file.ModelDoc.ClearSelection2(true);
                if (!ann.Select3(false, null)) {
                    throw new InvalidOperationException(
                        $"DeleteConstraint: could not select dimension '{wireDimName}' for deletion");
                }
                deleted = file.ModelDoc.Extension.DeleteSelection2(0);
            } else {
                deleted = manager.DeleteRelation(matches[0]);
            }
        } finally {
            sketchManager.InsertSketch(true);   // exit edit mode (toggle)
        }
        if (!deleted) {
            throw new InvalidOperationException(
                $"DeleteConstraint: DeleteRelation returned false for "
                + $"{(wireDimName ?? wireKind)} in '{sketchName}'");
        }

        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "SketchHandler.DeleteConstraint: deleted {Kind}{DimName} from {Name}",
            wireKind, wireDimName is null ? "" : $" ({wireDimName})", sketchName);

        return Inspect(file, sketchFeature);
    }

    // True iff `relation` is the constraint described by the wire payload. Dimensions match on
    // their unique name; geometric relations match on kind + the sketch-entity id multiset AND
    // the body-topology key multiset (so two relations on the same sketch entity but different
    // body faces/edges disambiguate, and a relation with only body refs still matches).
    private static bool RelationMatches(
            SketchRelation relation, string sketchName, string wireKind,
            string? wireDimName, List<string> wireKeys) {
        var disp = relation.GetDisplayDimension() as DisplayDimension;
        if (wireDimName is not null) {
            // Dimension: the FullName is unique within the model, so name alone identifies it.
            if (disp is null) return false;
            var dim = disp.GetDimension2(0);
            return dim is not null && string.Equals(dim.FullName, wireDimName, StringComparison.Ordinal);
        }
        // Geometric relation: must NOT be a dimension, kind must match, and its ref-key multiset
        // (sketch entities + body topology, uniformly) must equal the wire's.
        if (disp is not null) return false;
        var relKind = RelationConstraint.KindNameForTypeInt(relation.GetRelationType());
        if (!string.Equals(relKind, wireKind, StringComparison.Ordinal)) return false;
        return MultisetEqual(RefKeysFromRelation(relation, sketchName), wireKeys);
    }

    // One comparable key per ref. A SKETCH entity keys by its stable (sketch_name-scoped) id; BODY
    // topology (face/edge/vertex/axis) keys by a rounded capture (see BodyKey). The two key-builders
    // below — one reading the live relation, one reading the wire payload — emit the same key for
    // the same physical ref, so a single multiset comparison matches a constraint.
    private const string SketchKeyTag = "sk:";
    private const string BodyKeyTag = "body:";

    // Keys for a live relation's refs, off GetDefinitionEntities2() (the proxy that reports the
    // owning sketch truthfully). Un-keyable entities (e.g. SilhouetteEdge, which Capture can't
    // represent) are skipped — a constraint that depends on one simply won't match.
    private static List<string> RefKeysFromRelation(SketchRelation relation, string sketchName) {
        var keys = new List<string>();
        if (relation.GetDefinitionEntities2() is not object[] entities) return keys;
        foreach (var entity in entities) {
            if (entity is SketchSegment or SketchPoint) {
                try {
                    var sid = DefinitionCapture.SketchEntityIdFor(entity, sketchName);
                    keys.Add($"{SketchKeyTag}{sid.EntityKind}:{sid.Id}");
                } catch (InvalidOperationException) { /* unclassifiable sketch entity — skip */ }
            } else if (DefinitionCapture.Capture(entity) is Definition captured) {
                keys.Add(BodyKeyTag + BodyKey(captured));
            }
        }
        return keys;
    }

    // Keys for the wire constraint's refs. A sketch ref carries its SketchEntityId as the `id`
    // object → keyed directly. A body ref is a geometric Definition whose floats came through a
    // prior, differently-rounded capture, so we DON'T compare its JSON directly: resolve it to a
    // live entity (DefinitionResolver does the tolerance-aware geometric match) and re-capture in
    // this session, yielding a key comparable to RefKeysFromRelation. A body ref that won't resolve
    // throws — the referenced geometry is gone, so the constraint can't be matched.
    private static List<string> RefKeysFromWire(File file, Sketch sketch, JsonArray? refs) {
        var keys = new List<string>();
        if (refs is null) return keys;
        foreach (var refNode in refs) {
            if (refNode is not JsonObject obj) continue;
            if (obj["id"] is JsonObject idObj) {
                var ek = idObj["entity_kind"]?.GetValue<string>();
                var idVal = idObj["id"]?.GetValue<long>();
                if (ek is not null && idVal is not null) keys.Add($"{SketchKeyTag}{ek}:{idVal.Value}");
                continue;
            }
            var def = Definition.FromJson(refNode)
                ?? throw new ArgumentException(
                    $"DeleteConstraint: body ref {refNode.ToJsonString()} is not a valid Definition");
            var live = DefinitionResolver.Resolve(file, def, sketch)
                ?? throw new InvalidOperationException(
                    $"DeleteConstraint: body ref ({def.GetType().Name}) did not resolve to a live entity — "
                    + "the referenced face/edge/vertex may have changed; can't match the constraint.");
            var captured = DefinitionCapture.Capture(live)
                ?? throw new InvalidOperationException(
                    $"DeleteConstraint: resolved body ref ({live.GetType().Name}) could not be re-captured.");
            keys.Add(BodyKeyTag + BodyKey(captured));
        }
        return keys;
    }

    // A rounded, canonical key for a body-topology Definition. The captured geometry (face
    // centroid, edge endpoints, parent-body mass props) carries sub-nanometer float jitter that
    // differs between two captures of the SAME entity (e.g. a centroid component reads -2.9e-20 in
    // one capture and exactly 0 in another), so raw JSON equality is too brittle. Round every
    // number to 9 decimals (snapping |x|<1e-9 to 0 — well below the 1e-7 geometry tolerance) so
    // the same physical entity keys identically regardless of the jitter. This is the "fuzzy" part.
    private static string BodyKey(Definition def) =>
        RoundJsonNumbers(def.ToJson())?.ToJsonString() ?? "";

    private static JsonNode? RoundJsonNumbers(JsonNode? node) {
        switch (node) {
            case JsonObject obj: {
                var o = new JsonObject();
                foreach (var kv in obj) o[kv.Key] = RoundJsonNumbers(kv.Value);
                return o;
            }
            case JsonArray arr: {
                var a = new JsonArray();
                foreach (var item in arr) a.Add(RoundJsonNumbers(item));
                return a;
            }
            case JsonValue val:
                if (val.TryGetValue<double>(out var d)) {
                    return JsonValue.Create(Math.Abs(d) < 1e-9 ? 0.0 : Math.Round(d, 9));
                }
                return val.DeepClone();
            default:
                return node?.DeepClone();
        }
    }

    // Order-independent multiset equality. Empty == empty is true; the caller guards against the
    // both-empty (no identifiable refs) case.
    private static bool MultisetEqual(List<string> a, List<string> b) {
        if (a.Count != b.Count) return false;
        var sa = a.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sb = b.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (var i = 0; i < sa.Count; i++) {
            if (!string.Equals(sa[i], sb[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // Compact ref-key list for error messages — body keys are big JSON, so collapse them.
    private static string DescribeKeys(List<string> keys) {
        if (keys.Count == 0) return "[]";
        var parts = keys.Select(k => k.StartsWith(BodyKeyTag, StringComparison.Ordinal)
            ? "body(geom)" : k[SketchKeyTag.Length..]);
        return "[" + string.Join(", ", parts) + "]";
    }

    // SW dimension FullName is "<dimName>@<ownerFeature>@<docName>" (e.g.
    // "D1@Sketch1@Part1.Part") — sometimes the 2-part "<dimName>@<ownerFeature>".
    // The owning feature (the sketch) is always the SECOND '@'-delimited component,
    // NOT the last (that's the document). Returns null if there's no owner component.
    private static string? OwnerFromDimName(string dimName) {
        var parts = dimName.Split('@');
        return parts.Length >= 2 ? parts[1] : null;
    }

    // Final Inspect pass after all composite parsers run. Patterned relations
    // are SW-internal structural artifacts that never appear as user-authorable
    // wire constraints — anything still tagged "Patterned" after PolygonParser /
    // PatternParser have had their turn is leftover junk and gets stripped here.
    private static void StripLeftoverPatternedConstraints(JsonArray constraintsArr) {
        var kindPatterned = nameof(Sketches.ConstraintKind.Patterned);
        for (int i = constraintsArr.Count - 1; i >= 0; i--) {
            var node = constraintsArr[i] as JsonObject;
            if (node?["kind"]?.GetValue<string>() == kindPatterned) {
                constraintsArr.RemoveAt(i);
            }
        }
    }

    private static Feature? FindSketchFeatureByName(File file, string sketchName) {
        var feat = file.ModelDoc.IFirstFeature();
        while (feat is not null) {
            if (string.Equals(feat.Name, sketchName, StringComparison.Ordinal)
                && feat.GetSpecificFeature2() is Sketch) {
                return feat;
            }
            feat = feat.IGetNextFeature();
        }
        return null;
    }

    // Selects every resolved live entity onto SW selection mark 0 — the slot
    // ModelDoc.SketchAddConstraints / RelationManager.AddRelation / AddDimension2 read from.
    // Goes through Definition.SelectLive (not Definition.Select) so refs that resolved
    // through DefinitionResolver in AddConstraints land on the right selection path.
    //
    // SW quirk: Select4 / SketchAddConstraints / AddRelation do NOT clear prior selection.
    // Without an explicit ClearSelection2 here, each constraint's selection accumulates with
    // every previous constraint's — by the 12th constraint we'd have 14+ entities selected on
    // mark 0, and AddDimension2 fails because it can't tell which two to dimension.
    internal static void SelectAllForConstraint(File file, IReadOnlyList<object> resolved) {
        file.ModelDoc.ClearSelection2(true);
        for (var i = 0; i < resolved.Count; i++) {
            if (!Definition.SelectLive(file, resolved[i], 0)) {
                throw new InvalidOperationException(
                    $"SketchConstraint ref[{i}]: SolidWorks rejected selection of {resolved[i].GetType().Name}");
            }
        }
    }

    // ---- private helpers ---------------------------------------------------------

    private static void EnterSketchForInspect(File file, Feature sketchFeature) {
        var sketchManager = file.ModelDoc.SketchManager;
        // SW quirk: must exit any active sketch before entering this one — InsertSketch toggles.
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
        sketchFeature.Select2(false, 0);
        file.ModelDoc.EditSketch();
        if (sketchManager.ActiveSketch is null) {
            throw new InvalidOperationException(
                $"SketchHandler.Inspect: failed to enter sketch '{sketchFeature.Name}' (ActiveSketch null after EditSketch)");
        }
    }

    private static void ExitSketch(File file) {
        var sketchManager = file.ModelDoc.SketchManager;
        if (sketchManager.ActiveSketch is not null) {
            sketchManager.InsertSketch(true);
        }
    }

    // Reads the sketch's host plane via GetReferenceEntity. The refType int is unreliable per
    // the SolidWorks-quirks register; dispatch on runtime type instead and let
    // DefinitionCapture.Capture(object?) route to the right Definition flavor.
    private static JsonNode CapturePlane(Sketch sketch) {
        int refType = 0;
        var refEntity = sketch.GetReferenceEntity(ref refType);
        if (refEntity is null) {
            throw new InvalidOperationException(
                "SketchHandler.Inspect: sketch.GetReferenceEntity returned null — sketch has no host plane");
        }
        var def = DefinitionCapture.Capture(refEntity)
            ?? throw new InvalidOperationException(
                $"SketchHandler.Inspect: sketch reference entity of type {refEntity.GetType().Name} " +
                "did not capture as a Definition");
        return def.ToJson();
    }

    // Emit sketch entities AND record their (kind, id) keys into a hashset that
    // SketchConstraint.ReadAll uses to gate constraint refs.
    //
    // The hashset is populated in lock-step with the entity walk — anything we
    // emit into `entities` lands in `emittedIds`. Additionally, non-emitted
    // SketchPoint markers (curve endpoints / arc centers / etc.) are added to the
    // hashset but NOT to entities (they round-trip via the parent segment, but
    // SW's RelationManager refs them by id so the constraint walker needs to
    // know they're part of THIS sketch). Only swSketchPointType_External — body
    // silhouette projections — is excluded; those can't be reauthored on target.
    private static (JsonArray Entities, HashSet<(string Kind, long Id)> EmittedIds) ReadEntities(Sketch sketch, string sketchName) {
        var arr = new JsonArray();
        var emittedIds = new HashSet<(string, long)>();

        var segments = sketch.GetSketchSegments() as object[];
        if (segments is not null) {
            foreach (var entry in segments) {
                if (entry is null) continue;
                if (entry is not SketchSegment seg) {
                    throw new InvalidOperationException(
                        $"SketchHandler.Inspect: sketch segment entry of unexpected type {entry.GetType().Name}");
                }
                var def = DefinitionCapture.Capture(seg, sketchName);
                var node = def.ToJson();
                arr.Add(node);
                CollectSketchEntityIds(node, sketchName, emittedIds);
            }
        }

        var points = sketch.GetSketchPoints2() as object[];
        if (points is not null) {
            foreach (var entry in points) {
                if (entry is null) continue;
                if (entry is not SketchPoint pt) {
                    throw new InvalidOperationException(
                        $"SketchHandler.Inspect: sketch point entry of unexpected type {entry.GetType().Name}");
                }
                if (pt.Type == (int)swSketchPointType_e.swSketchPointType_External) continue;

                // Only standalone User / MidPoint / VirtualSharp points are emitted;
                // line/arc endpoints are reachable via the parent segment and would
                // round-trip as duplicate entities otherwise. Segment-child point ids
                // ARE in `emittedIds` because the segment walk above called
                // CollectSketchEntityIds on each segment's def (which descends into
                // start/end/center). We MUST NOT add internal-type point ids to
                // `emittedIds` from this loop — those are points SW reports via
                // GetSketchPoints2 that are NOT children of any emitted segment (true
                // orphans: dimension-handles, construction artifacts, etc.). Leaving
                // their id in emittedIds would let ConstraintHelpers.ReadRelationRefs
                // pass constraints through; on target the C# resolver matches by id
                // only and may bind to a coincidentally-id-matched entity, pulling
                // sketch geometry to the wrong place (e.g. target's arc.end at the
                // id matching the source's orphan point — Coincident drags line.end
                // to arc.end).
                var ptType = pt.Type;
                if (ptType != (int)swSketchPointType_e.swSketchPointType_User &&
                    ptType != (int)swSketchPointType_e.swSketchPointType_MidPoint &&
                    ptType != (int)swSketchPointType_e.swSketchPointType_VirtualSharp) {
                    continue;
                }
                var def = DefinitionCapture.Capture(pt, sketchName);
                var node = def.ToJson();
                CollectSketchEntityIds(node, sketchName, emittedIds);
                arr.Add(node);
            }
        }

        return (arr, emittedIds);
    }

    // Recursively walks a captured entity JsonNode and records every nested
    // SketchEntityId whose sketch_name matches the owning sketch. SW's
    // SketchRelation can reference any internal id (Line endpoints, Circle/Arc
    // center, Spline control points, Ellipse axis points, Polygon construction
    // circle + per-segment ids, pattern instance ids, etc.), so the constraint
    // walker in ConstraintHelpers.ReadRelationRefs needs all of them in
    // emittedIds to validate same-sketch refs. Refs to a different sketch_name
    // are skipped — they belong to that sketch's own emission. Defensive on
    // shape: any missing / mistyped field is silently ignored.
    private static void CollectSketchEntityIds(JsonNode? node, string sketchName, HashSet<(string Kind, long Id)> ids) {
        switch (node) {
            case JsonObject obj:
                if (obj["id"] is JsonObject idObj
                    && idObj["sketch_name"] is JsonValue snVal
                    && idObj["entity_kind"] is JsonValue ekVal
                    && idObj["id"] is JsonValue idVal
                    && snVal.TryGetValue<string>(out var sn)
                    && sn == sketchName
                    && ekVal.TryGetValue<string>(out var ek)
                    && idVal.TryGetValue<long>(out var id)) {
                    ids.Add((ek, id));
                }
                foreach (var kvp in obj) {
                    CollectSketchEntityIds(kvp.Value, sketchName, ids);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) {
                    CollectSketchEntityIds(item, sketchName, ids);
                }
                break;
        }
    }

    private static void SelectPlane(File file, object planeLive) {
        var sd = MakeMarkSelectData(file, 0);
        switch (planeLive) {
            case Face2 face:
                ((IEntity)face).Select4(false, sd);
                break;
            case RefPlane plane:
                ((Feature)plane).Select2(false, 0);
                break;
            case Feature feat:
                feat.Select2(false, 0);
                break;
            default:
                throw new InvalidOperationException(
                    $"SketchHandler.Add: plane resolved to unsupported runtime type {planeLive.GetType().Name} " +
                    "(expected Face2, RefPlane, or Feature)");
        }
    }

    // Adds one entity payload to the live SW sketch and returns the entity itself
    // (typed as ISketchEntity) — the entity stores its own live ref(s) at Add time
    // and captures itself via GetJson at end-of-flow. Both primitives and composites
    // implement ISketchEntity, so the entities list in SketchHandler.Add doesn't need
    // to distinguish between them.
    //
    // Wire dispatch: composites carry a `type` field (Polygon/Rectangle/Pattern/Offset)
    // and are NOT Definitions; primitives carry a `kind` field (sketch_line / ... ) and
    // ARE Definitions. Try the type-discriminator first, fall through to the Definition
    // path.
    private static ISketchEntity AddOneEntity(
            File file, Sketch sketch, SketchManager sketchManager,
            JsonNode entityNode, int index) {
        var typeStr = (entityNode as JsonObject)?["type"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(typeStr)) {
            return AddOneComposite(file, sketch, sketchManager, entityNode, index, typeStr!);
        }

        var def = Definition.FromJson(entityNode)
            ?? throw new ArgumentException(
                $"SketchHandler.Add: entities[{index}] did not deserialize to a Definition");
        if (def is not SketchEntityDefinition) {
            throw new ArgumentException(
                $"SketchHandler.Add: entities[{index}] deserialized to {def.GetType().Name}, " +
                "expected a SketchEntityDefinition flavor (Line/Circle/Arc/Point/etc.)");
        }

        // Per-record Add — no virtual interface, dispatch by runtime type. SW quirk: Spline.Add
        // takes a third ModelDoc2 arg (it runs swCommands_ConvertToModif on Generic splines).
        // Each Add returns `this` typed as the concrete record so the switch yields the
        // ISketchEntity-typed result without an extra cast.
        return def switch {
            Line l           => l.Add(sketch, sketchManager),
            Circle c         => c.Add(sketch, sketchManager),
            Arc a            => a.Add(sketch, sketchManager),
            Ellipse el       => el.Add(sketch, sketchManager),
            EllipticalArc ea => ea.Add(sketch, sketchManager),
            PointEntity pt   => pt.Add(sketch, sketchManager),
            Spline sp        => sp.Add(sketch, sketchManager, file.ModelDoc),
            Parabola pa      => pa.Add(sketch, sketchManager),
            _ => throw new ArgumentException(
                $"SketchHandler.Add: entities[{index}] runtime type {def.GetType().Name} " +
                "has no Add dispatch in this handler"),
        };
    }

    // Composite (type-discriminated) dispatch. Composites expand inside SW into multiple
    // primitives but return a single composite entity — the response carries one composite
    // entry, never decomposed.
    private static ISketchEntity AddOneComposite(
            File file, Sketch sketch, SketchManager sketchManager,
            JsonNode entityNode, int index, string typeStr) {
        switch (typeStr) {
            case SketchPolygon.TypeName: {
                var poly = entityNode.Deserialize<SketchPolygon>(Definition.JsonOptions)
                    ?? throw new ArgumentException(
                        $"SketchHandler.Add: entities[{index}] (type={typeStr}) did not deserialize");
                return poly.Add(sketchManager);
            }
            case SketchLinearPattern.TypeName: {
                var pat = entityNode.Deserialize<SketchLinearPattern>(Definition.JsonOptions)
                    ?? throw new ArgumentException(
                        $"SketchHandler.Add: entities[{index}] (type={typeStr}) did not deserialize");
                return pat.Add(file, sketch, sketchManager);
            }
            case SketchCircularPattern.TypeName: {
                var pat = entityNode.Deserialize<SketchCircularPattern>(Definition.JsonOptions)
                    ?? throw new ArgumentException(
                        $"SketchHandler.Add: entities[{index}] (type={typeStr}) did not deserialize");
                return pat.Add(file, sketch, sketchManager);
            }
            default:
                throw new ArgumentException(
                    $"SketchHandler.Add: entities[{index}] composite type '{typeStr}' " +
                    "has no Add dispatch (Rectangle/Offset are not yet wired)");
        }
    }

    private static SelectData MakeMarkSelectData(File file, int mark) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        return sd;
    }
}
