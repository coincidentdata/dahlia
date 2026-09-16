using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class FilletHandler {
    public const string TypeName = "Fillet";

    // SW quirk: variant lives on the FeatureData, not the type name.
    public const string SwTypeName = "Fillet";

    private const int MarkConstantSimple = 1;
    private const int MarkFaceSetA = 2;
    private const int MarkFaceSetB = 4;
    private const int MarkFullRoundSetA = 2;
    private const int MarkFullRoundSetB = 512;  // SW quirk: center set uses 512, not a sequential value.
    private const int MarkFullRoundSetC = 4;

    public static JsonNode Add(File file, JsonNode input) {
        var kind = (input["kind"]?.GetValue<string>()) ?? "ConstantRadius";
        return kind switch {
            "ConstantRadius" => AddConstantRadius(file, input),
            "Face"           => AddFaceFillet(file, input),
            "FullRound"      => AddFullRoundFillet(file, input),
            _                => throw new ArgumentException($"Fillet: unknown kind '{kind}'"),
        };
    }

    private static JsonNode AddConstantRadius(File file, JsonNode input) {
        var args = ConstantRadiusArgs.Parse(input);
        if (args.Edges.Count == 0) {
            throw new ArgumentException("Fillet[ConstantRadius]: edges list is empty");
        }

        file.ModelDoc.ClearSelection2(true);
        Definition.SelectAll(file, args.Edges, mark: MarkConstantSimple, label: "Fillet edge");

        var fm = file.ModelDoc.FeatureManager;
        var data = (SimpleFilletFeatureData2)fm.CreateDefinition((int)swFeatureNameID_e.swFmFillet);
        data.Initialize((int)swFeatureFilletType_e.swFeatureFilletType_Simple);

        data.PropagateToTangentFaces = args.TangentPropagation;
        ApplyAsymmetric(data, args.Symmetric, args.Radius, args.RadiusB ?? args.Radius);
        ApplyProfile(data, args.Profile, args.ConicValue);
        data.RoundCorners = args.RoundCorners;
        data.OverflowType = (int)args.Overflow;

        var feature = (Feature?)fm.CreateFeature(data)
            ?? throw new InvalidOperationException(
                "Fillet[ConstantRadius]: CreateFeature returned null — check edges resolved and radius is non-zero against the local geometry");

        SldworksLog.Information("FilletHandler.Add[ConstantRadius]: created {Name} on {Count} seed(s)",
            feature.Name, args.Edges.Count);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    private static JsonNode AddFaceFillet(File file, JsonNode input) {
        var args = FaceFilletArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);
        // Sequence selections in strictly decreasing mark order: face_set_b (4) → face_set_a (2).
        Definition.SelectAll(file, args.FaceSetB, mark: MarkFaceSetB, label: "Fillet face_set_b");
        Definition.SelectAll(file, args.FaceSetA, mark: MarkFaceSetA, label: "Fillet face_set_a");

        var fm = file.ModelDoc.FeatureManager;
        var data = (SimpleFilletFeatureData2)fm.CreateDefinition((int)swFeatureNameID_e.swFmFillet);
        data.Initialize((int)swFeatureFilletType_e.swFeatureFilletType_Face);

        data.PropagateToTangentFaces = args.TangentPropagation;

        switch (args.FaceType) {
            case FaceType.Radius:
                if (args.Radius is null) {
                    throw new ArgumentException("Fillet[Face/Radius]: radius is required");
                }
                ApplyAsymmetric(data, args.Symmetric, args.Radius.Value,
                    args.RadiusB ?? args.Radius.Value);
                ApplyProfile(data, args.Profile, args.ConicValue);
                break;
            case FaceType.ChordWidth:
                if (args.ChordWidth is null) {
                    throw new ArgumentException("Fillet[Face/ChordWidth]: chord_width is required");
                }
                data.DefaultRadius = args.ChordWidth.Value;
                data.ConstantWidth = true;
                ApplyProfile(data, args.Profile, args.ConicValue);
                break;
            case FaceType.HoldLines:
                // Hold-line entities are written via ModifyDefinition after CreateFeature.
                ApplyProfile(data, args.Profile, args.ConicValue);
                break;
        }

        var feature = (Feature?)fm.CreateFeature(data);
        if (feature is null) {
            throw new InvalidOperationException(
                "Fillet[Face]: CreateFeature returned null — check face sets resolve and the help point disambiguates the round side");
        }

        var needsModify =
            (args.FaceType == FaceType.HoldLines && args.HoldLines.Count > 0)
            || args.HelpPoint is not null;
        if (needsModify) {
            try {
                ModifyHoldLinesAndHelpPoint(file, feature, args);
            } catch (Exception ex) {
                // Delete the half-created feature on failure.
                feature.Select2(false, 0);
                file.ModelDoc.Extension.DeleteSelection2(0);
                throw new InvalidOperationException(
                    $"Fillet[Face]: failed to apply hold lines / help point — {ex.Message}", ex);
            }
        }

        SldworksLog.Information("FilletHandler.Add[Face]: created {Name} (face_type={FaceType})",
            feature.Name, args.FaceType);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    private static JsonNode AddFullRoundFillet(File file, JsonNode input) {
        var args = FullRoundArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);
        // Sequence selections in strictly decreasing mark order:
        // face_set_b/center (512) → face_set_c (4) → face_set_a (2).
        Definition.SelectAll(file, args.FaceSetB, mark: MarkFullRoundSetB, label: "Fillet face_set_b (center)");
        Definition.SelectAll(file, args.FaceSetC, mark: MarkFullRoundSetC, label: "Fillet face_set_c");
        Definition.SelectAll(file, args.FaceSetA, mark: MarkFullRoundSetA, label: "Fillet face_set_a");

        var fm = file.ModelDoc.FeatureManager;
        var data = (SimpleFilletFeatureData2)fm.CreateDefinition((int)swFeatureNameID_e.swFmFillet);
        data.Initialize((int)swFeatureFilletType_e.swFeatureFilletType_FullRound);

        data.PropagateToTangentFaces = args.TangentPropagation;

        var feature = (Feature?)fm.CreateFeature(data)
            ?? throw new InvalidOperationException(
                "Fillet[FullRound]: CreateFeature returned null — check the three face sets resolve");

        SldworksLog.Information("FilletHandler.Add[FullRound]: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    private static void ApplyAsymmetric(SimpleFilletFeatureData2 data, bool symmetric, double radiusA, double radiusB) {
        // SW quirk: AsymmetricFillet must be set BEFORE DefaultDistance.
        data.AsymmetricFillet = !symmetric;
        data.DefaultRadius = radiusA;
        data.DefaultDistance = symmetric ? radiusA : radiusB;
    }

    private static void ApplyProfile(SimpleFilletFeatureData2 data, WireProfile profile, double? conicValue) {
        // SW quirk: ConicType must be set before DefaultConicRhoOrRadius;
        // CurvatureContinuous wins over ConicType.
        switch (profile) {
            case WireProfile.Circular:
                data.ConicTypeForCrossSectionProfile = (int)swFeatureFilletProfileType_e.swFeatureFilletCircular;
                break;
            case WireProfile.ConicRho:
                data.ConicTypeForCrossSectionProfile = (int)swFeatureFilletProfileType_e.swFeatureFilletConicRho;
                if (conicValue is double rho) data.DefaultConicRhoOrRadius = rho;
                break;
            case WireProfile.ConicRadius:
                data.ConicTypeForCrossSectionProfile = (int)swFeatureFilletProfileType_e.swFeatureFilletConicRadius;
                if (conicValue is double r) data.DefaultConicRhoOrRadius = r;
                break;
            case WireProfile.ConicRhoZeroChamfer:
                data.ConicTypeForCrossSectionProfile = (int)swFeatureFilletProfileType_e.swFeatureFilletConicRhoZeroChamfer;
                if (conicValue is double zr) data.DefaultConicRhoOrRadius = zr;
                break;
            case WireProfile.CurvatureContinuous:
                data.CurvatureContinuous = true;
                break;
        }
    }

    private static void ModifyHoldLinesAndHelpPoint(File file, Feature feature, FaceFilletArgs args) {
        var data = feature.GetDefinition() as ISimpleFilletFeatureData2
            ?? throw new InvalidOperationException(
                "Fillet[Face]: feature.GetDefinition() did not return ISimpleFilletFeatureData2 on post-create modify");

        if (!data.IAccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException("Fillet[Face]: AccessSelections failed during post-create modify");
        }
        try {
            if (args.FaceType == FaceType.HoldLines && args.HoldLines.Count > 0) {
                var live = new List<object>(args.HoldLines.Count);
                for (var i = 0; i < args.HoldLines.Count; i++) {
                    var resolved = DefinitionResolver.Resolve(file, args.HoldLines[i])
                        ?? throw new InvalidOperationException(
                            $"Fillet[Face/HoldLines]: hold_lines[{i}] did not resolve to live geometry");
                    live.Add(resolved);
                }
                // SW quirk: HoldLines setter wants DispatchWrapper[]; bare object[] crashes the marshaller.
                ((SimpleFilletFeatureData2)data).HoldLines = live.Select(e => new DispatchWrapper(e)).ToArray();
            }

            if (args.HelpPoint is { } hp) {
                // SW quirk: HelpPoint setter wants a live Vertex; resolve to the nearest one.
                var nearest = NearestVertex(file.ModelDoc, hp);
                if (nearest is null) {
                    SldworksLog.Warning(
                        "Fillet[Face]: help_point ({X}, {Y}, {Z}) — no vertex within tol; leaving HelpPoint unset",
                        hp.X, hp.Y, hp.Z);
                } else {
                    ((SimpleFilletFeatureData2)data).HelpPoint = nearest;
                }
            }

            var ok = feature.ModifyDefinition(data, file.ModelDoc, null);
            if (!ok) {
                throw new InvalidOperationException("Fillet[Face]: ModifyDefinition returned false");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static Vertex? NearestVertex(ModelDoc2 doc, Point3D target) {
        var part = doc as PartDoc;
        if (part is null) return null;
        var bodies = (object[]?)part.GetBodies2((int)swBodyType_e.swSolidBody, false) ?? Array.Empty<object>();
        Vertex? best = null;
        var bestDist = double.PositiveInfinity;
        foreach (var b in bodies) {
            if (b is not Body2 body) continue;
            var verts = (object[]?)body.GetVertices() ?? Array.Empty<object>();
            foreach (var v in verts) {
                if (v is not Vertex vert) continue;
                if (vert.GetPoint() is not double[] p || p.Length < 3) continue;
                var dx = p[0] - target.X;
                var dy = p[1] - target.Y;
                var dz = p[2] - target.Z;
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestDist) { bestDist = d2; best = vert; }
            }
        }
        return bestDist <= 1e-12 ? best : null;
    }

    // Edit an existing fillet IN PLACE (GetDefinition -> AccessSelections -> set scalars ->
    // ModifyDefinition); no delete/re-add, so the feature name + downstream refs survive.
    // `input` is a FULL fillet payload (same shape Inspect emits), parsed by the existing
    // per-kind Args so edit and create share validation. Applies only the parametric
    // scalars/settings Inspect reads (radius, radius_b, symmetric, profile/conic, tangent
    // propagation, round corners, overflow, chord width); the edge/face/hold-line SELECTIONS
    // are kept exactly as built. ISimpleFilletFeatureData2 IS settable (unlike the read-only
    // Sweep/Loft co-classes), and the Add path already uses ModifyDefinition on fillets, so
    // there's no crash risk.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var requestedKind = (input["kind"]?.GetValue<string>()) ?? "ConstantRadius";

        var data = feature.GetDefinition() as ISimpleFilletFeatureData2
            ?? throw new InvalidOperationException(
                $"Fillet.Edit: feature {feature.Name} does not expose ISimpleFilletFeatureData2");
        var cdata = (SimpleFilletFeatureData2)data;

        // The fillet TYPE (Simple/Face/FullRound) is bound to the selection shape — a kind
        // change re-points selections, which is a different feature, not an in-place edit.
        var builtKind = (swFeatureFilletType_e)data.Type switch {
            swFeatureFilletType_e.swFeatureFilletType_Simple    => "ConstantRadius",
            swFeatureFilletType_e.swFeatureFilletType_Face      => "Face",
            swFeatureFilletType_e.swFeatureFilletType_FullRound => "FullRound",
            var t => throw new InvalidOperationException($"Fillet.Edit: unsupported fillet type {t}"),
        };
        if (requestedKind != builtKind) {
            throw new InvalidOperationException(
                $"Fillet.Edit: cannot change kind '{builtKind}' -> '{requestedKind}' in place "
                + "(re-points the selection shape) — delete and re-add.");
        }

        data.AccessSelections(file.ModelDoc, null);
        try {
            switch (builtKind) {
                case "ConstantRadius": {
                    var args = ConstantRadiusArgs.Parse(input);
                    RepointConstantRadiusEdgesIfChanged(file, data, args.Edges, feature.Name);
                    cdata.PropagateToTangentFaces = args.TangentPropagation;
                    ApplyAsymmetric(cdata, args.Symmetric, args.Radius, args.RadiusB ?? args.Radius);
                    ApplyProfile(cdata, args.Profile, args.ConicValue);
                    cdata.RoundCorners = args.RoundCorners;
                    cdata.OverflowType = (int)args.Overflow;
                    break;
                }
                case "Face": {
                    var args = FaceFilletArgs.Parse(input);
                    RepointFaceSetIfChanged(file, data, swSimpleFilletWhichFaces_e.swFaceFilletSet1,
                        args.FaceSetA, feature.Name, "face_set_a");
                    RepointFaceSetIfChanged(file, data, swSimpleFilletWhichFaces_e.swFaceFilletSet2,
                        args.FaceSetB, feature.Name, "face_set_b");
                    cdata.PropagateToTangentFaces = args.TangentPropagation;
                    switch (args.FaceType) {
                        case FaceType.Radius:
                            ApplyAsymmetric(cdata, args.Symmetric, args.Radius!.Value,
                                args.RadiusB ?? args.Radius!.Value);
                            ApplyProfile(cdata, args.Profile, args.ConicValue);
                            break;
                        case FaceType.ChordWidth:
                            cdata.DefaultRadius = args.ChordWidth!.Value;
                            cdata.ConstantWidth = true;
                            ApplyProfile(cdata, args.Profile, args.ConicValue);
                            break;
                        case FaceType.HoldLines:
                            // Hold-line entities + help point are SELECTIONS, kept as built;
                            // only profile/tangent are scalar-editable here.
                            ApplyProfile(cdata, args.Profile, args.ConicValue);
                            break;
                    }
                    break;
                }
                case "FullRound": {
                    var args = FullRoundArgs.Parse(input);
                    // Inspect maps: set_a -> Set1, set_b -> CenterSet, set_c -> Set2.
                    RepointFaceSetIfChanged(file, data, swSimpleFilletWhichFaces_e.swFullRoundFilletSet1,
                        args.FaceSetA, feature.Name, "face_set_a");
                    RepointFaceSetIfChanged(file, data, swSimpleFilletWhichFaces_e.swFullRoundFilletCenterSet,
                        args.FaceSetB, feature.Name, "face_set_b");
                    RepointFaceSetIfChanged(file, data, swSimpleFilletWhichFaces_e.swFullRoundFilletSet2,
                        args.FaceSetC, feature.Name, "face_set_c");
                    // No radius (it's geometry-derived); tangent propagation is the only scalar.
                    cdata.PropagateToTangentFaces = args.TangentPropagation;
                    break;
                }
            }

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"Fillet.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "radius invalid against the local geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream. NOT AfterFeature: a mid-tree
        // fillet must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("FilletHandler.Edit: {Name} ({Kind})", feature.Name, builtKind);
        return Inspect(file, feature);
    }

    // Re-point a ConstantRadius fillet onto a (possibly) different edge set in place. The
    // incoming `edges` defs arrive in the Edit payload (ConstantRadiusArgs.Edges — the SAME
    // refs Add resolves+selects). Unlike Add (select-then-CreateFeature), here we resolve them
    // to live edges and set the data interface's `Edges` property directly inside the open
    // AccessSelections block, before ModifyDefinition.
    //
    // A ConstantRadius fillet can also seed off Loops / GetFaces(SingleRadius) / Features
    // (a UI-authored fillet picking a loop, face, or whole feature). An `edges` re-point can't
    // address those slots; if the built feature carries seeds there we refuse rather than
    // silently drop them.
    private static void RepointConstantRadiusEdgesIfChanged(
        File file, ISimpleFilletFeatureData2 data, IReadOnlyList<Definition> edgeDefs, string featureName) {
        if (edgeDefs.Count == 0) {
            // ConstantRadiusArgs.Parse doesn't enforce non-empty (Add does separately); keep the
            // built selection rather than clearing it to empty.
            return;
        }

        var resolvedLive = new List<Edge>(edgeDefs.Count);
        for (var i = 0; i < edgeDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, edgeDefs[i]);
            if (live is null) {
                throw new InvalidOperationException(
                    $"Fillet.Edit[ConstantRadius]: edges[{i}] ({edgeDefs[i].GetType().Name}) did "
                    + $"not resolve to a live entity on '{featureName}'.");
            }
            if (live is not Edge edge) {
                throw new InvalidOperationException(
                    $"Fillet.Edit[ConstantRadius]: edges[{i}] resolved to {live.GetType().Name}, "
                    + $"expected Edge on '{featureName}' (Loop/Face/Feature seed re-pointing is out of scope).");
            }
            resolvedLive.Add(edge);
        }

        if (HasNonEdgeSeeds(data)) {
            throw new InvalidOperationException(
                $"Fillet.Edit[ConstantRadius]: '{featureName}' carries Loop/Face/Feature seeds; "
                + "re-pointing via `edges` would drop them — delete and re-add to change those seeds.");
        }

        // SW quirk: SAFEARRAY-of-IDispatch setters want DispatchWrapper[]; bare object[] crashes
        // the marshaller (same as HoldLines / AddRelation). `set_Edges(Object)` is the
        // reflection-confirmed setter on ISimpleFilletFeatureData2.
        data.Edges = resolvedLive.Select(e => new DispatchWrapper(e)).ToArray();
        SldworksLog.Information(
            "FilletHandler.Edit[ConstantRadius]: re-pointed '{Name}' onto {Count} edge(s)",
            featureName, resolvedLive.Count);
    }

    // Re-point one face set of a Face / FullRound fillet in place. `which` is the
    // swSimpleFilletWhichFaces_e slot (the SAME code Inspect's GetFaces reads). Resolves the
    // requested face defs to live Face2s and writes them via SetFaces(which, faces).
    private static void RepointFaceSetIfChanged(
        File file, ISimpleFilletFeatureData2 data, swSimpleFilletWhichFaces_e which,
        IReadOnlyList<Definition> faceDefs, string featureName, string label) {
        if (faceDefs.Count == 0) {
            // The per-kind Args already reject empty face sets; defensive no-op.
            return;
        }

        var resolvedLive = new List<Face2>(faceDefs.Count);
        for (var i = 0; i < faceDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, faceDefs[i]);
            if (live is null) {
                throw new InvalidOperationException(
                    $"Fillet.Edit: {label}[{i}] ({faceDefs[i].GetType().Name}) did not resolve "
                    + $"to a live entity on '{featureName}'.");
            }
            if (live is not Face2 face) {
                throw new InvalidOperationException(
                    $"Fillet.Edit: {label}[{i}] resolved to {live.GetType().Name}, expected Face "
                    + $"on '{featureName}'.");
            }
            resolvedLive.Add(face);
        }

        // SW quirk: SetFaces is a per-WhichFaceList method (NOT a settable Faces property on
        // this interface). It takes Object (SAFEARRAY-of-IDispatch) — DispatchWrapper[] for the
        // marshaller, same pattern as the edge slot.
        data.SetFaces((int)which, resolvedLive.Select(f => new DispatchWrapper(f)).ToArray());
        SldworksLog.Information(
            "FilletHandler.Edit: re-pointed '{Name}' {Label} onto {Count} face(s)",
            featureName, label, resolvedLive.Count);
    }

    private static bool HasNonEdgeSeeds(ISimpleFilletFeatureData2 data) {
        if (data.Loops is object[] loops && loops.Any(o => o is Loop2)) return true;
        if (data.GetFaces((int)swSimpleFilletWhichFaces_e.swSimpleFilletSingleRadius) is object[] faces
            && faces.Any(o => o is Face2)) return true;
        if (data.Features is object[] features && features.Any(o => o is Feature)) return true;
        return false;
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as ISimpleFilletFeatureData2
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} does not expose ISimpleFilletFeatureData2");

        // SW quirk: AccessSelections required before reading entities; missing Release wedges the doc.
        data.AccessSelections(file.ModelDoc, null);
        try {
            var swType = (swFeatureFilletType_e)data.Type;
            var affected = Definition.CaptureFaces(feature);
            return swType switch {
                swFeatureFilletType_e.swFeatureFilletType_Simple    => InspectConstantRadius(data, feature, affected),
                swFeatureFilletType_e.swFeatureFilletType_Face      => InspectFaceFillet(data, feature, affected),
                swFeatureFilletType_e.swFeatureFilletType_FullRound => InspectFullRoundFillet(data, feature, affected),
                _ => throw new InvalidOperationException(
                    $"Inspect(Fillet): unsupported fillet type {swType} on feature {feature.Name}"),
            };
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static JsonNode InspectConstantRadius(ISimpleFilletFeatureData2 data, Feature feature, JsonArray affected) {
        var (profile, conic) = ProfileFromData(data);
        return new JsonObject {
            ["type"] = TypeName,
            ["kind"] = "ConstantRadius",
            ["edges"] = CaptureConstantRadiusEdges(data),
            ["radius"] = data.DefaultRadius,
            ["radius_b"] = data.AsymmetricFillet ? data.DefaultDistance : null,
            ["symmetric"] = !data.AsymmetricFillet,
            ["profile"] = profile,
            ["conic_value"] = conic,
            ["tangent_propagation"] = data.PropagateToTangentFaces,
            ["round_corners"] = data.RoundCorners,
            ["overflow"] = OverflowToWire((swFilletOverFlowType_e)data.OverflowType),
            ["echo_affected_faces"] = affected,
            ["name"] = feature.Name,
        };
    }

    private static JsonNode InspectFaceFillet(ISimpleFilletFeatureData2 data, Feature feature, JsonArray affected) {
        var (profile, conic) = ProfileFromData(data);

        string faceType;
        if (data.GetHoldLineCount() > 0) {
            faceType = "HoldLines";
        } else if (data.ConstantWidth) {
            faceType = "ChordWidth";
        } else {
            faceType = "Radius";
        }

        var setA = CaptureFaceSet(data, swSimpleFilletWhichFaces_e.swFaceFilletSet1);
        var setB = CaptureFaceSet(data, swSimpleFilletWhichFaces_e.swFaceFilletSet2);

        var json = new JsonObject {
            ["type"] = TypeName,
            ["kind"] = "Face",
            ["face_set_a"] = setA,
            ["face_set_b"] = setB,
            ["face_type"] = faceType,
            ["profile"] = profile,
            ["conic_value"] = conic,
            ["tangent_propagation"] = data.PropagateToTangentFaces,
            ["help_point"] = HelpPointToWire(data),
            ["echo_affected_faces"] = affected,
            ["name"] = feature.Name,
        };

        if (faceType == "Radius") {
            json["radius"] = data.DefaultRadius;
            json["radius_b"] = data.AsymmetricFillet ? data.DefaultDistance : null;
            json["symmetric"] = !data.AsymmetricFillet;
        } else if (faceType == "ChordWidth") {
            json["chord_width"] = data.DefaultRadius;
        } else { // HoldLines
            json["hold_lines"] = CaptureHoldLines(data);
        }
        return json;
    }

    private static JsonNode InspectFullRoundFillet(ISimpleFilletFeatureData2 data, Feature feature, JsonArray affected) {
        return new JsonObject {
            ["type"] = TypeName,
            ["kind"] = "FullRound",
            ["face_set_a"] = CaptureFaceSet(data, swSimpleFilletWhichFaces_e.swFullRoundFilletSet1),
            ["face_set_b"] = CaptureFaceSet(data, swSimpleFilletWhichFaces_e.swFullRoundFilletCenterSet),
            ["face_set_c"] = CaptureFaceSet(data, swSimpleFilletWhichFaces_e.swFullRoundFilletSet2),
            ["tangent_propagation"] = data.PropagateToTangentFaces,
            ["echo_affected_faces"] = affected,
            ["name"] = feature.Name,
        };
    }

    private static JsonArray CaptureConstantRadiusEdges(ISimpleFilletFeatureData2 data) {
        // SW quirk: ConstantRadius seeds live across four slots — Edges, Loops,
        // GetFaces(swSimpleFilletSingleRadius), and Features.
        var arr = new JsonArray();

        if (data.Edges is object[] edges) {
            foreach (var entry in edges) {
                if (entry is Edge edge) arr.Add(DefinitionCapture.Capture(edge).ToJson());
            }
        }

        if (data.Loops is object[] loops) {
            foreach (var loopObj in loops) {
                if (loopObj is not Loop2 loop) continue;
                if (loop.GetEdges() is object[] loopEdges) {
                    foreach (var e in loopEdges) {
                        if (e is Edge edge) arr.Add(DefinitionCapture.Capture(edge).ToJson());
                    }
                }
            }
        }

        if (data.GetFaces((int)swSimpleFilletWhichFaces_e.swSimpleFilletSingleRadius) is object[] faces) {
            foreach (var entry in faces) {
                if (entry is Face2 face) arr.Add(DefinitionCapture.Capture(face).ToJson());
            }
        }

        if (data.Features is object[] features) {
            foreach (var entry in features) {
                if (entry is Feature feat) arr.Add(DefinitionCapture.Capture(feat).ToJson());
            }
        }

        return arr;
    }

    private static JsonArray CaptureFaceSet(ISimpleFilletFeatureData2 data, swSimpleFilletWhichFaces_e which) {
        var arr = new JsonArray();
        if (data.GetFaces((int)which) is object[] faces) {
            foreach (var entry in faces) {
                if (entry is Face2 face) arr.Add(DefinitionCapture.Capture(face).ToJson());
            }
        }
        return arr;
    }

    private static JsonArray CaptureHoldLines(ISimpleFilletFeatureData2 data) {
        var arr = new JsonArray();
        if (((SimpleFilletFeatureData2)data).HoldLines is object[] lines) {
            foreach (var entry in lines) {
                if (entry is Edge edge) arr.Add(DefinitionCapture.Capture(edge).ToJson());
            }
        }
        return arr;
    }

    private static JsonNode? HelpPointToWire(ISimpleFilletFeatureData2 data) {
        // HelpPoint comes back as a Vertex; emit x/y/z (wire schema is Point3D, not Definition).
        var hp = ((SimpleFilletFeatureData2)data).HelpPoint;
        if (hp is null) return null;
        if (hp is Vertex v && v.GetPoint() is double[] p && p.Length >= 3) {
            return new JsonObject { ["x"] = p[0], ["y"] = p[1], ["z"] = p[2] };
        }
        return null;
    }

    private static (string Profile, double? Conic) ProfileFromData(ISimpleFilletFeatureData2 data) {
        // SW quirk: CurvatureContinuous=true wins over ConicType.
        if (data.CurvatureContinuous) return ("CurvatureContinuous", null);
        var code = data.ConicTypeForCrossSectionProfile;
        return code switch {
            (int)swFeatureFilletProfileType_e.swFeatureFilletCircular            => ("Circular", null),
            (int)swFeatureFilletProfileType_e.swFeatureFilletConicRho            => ("ConicRho",            data.DefaultConicRhoOrRadius),
            (int)swFeatureFilletProfileType_e.swFeatureFilletConicRadius         => ("ConicRadius",         data.DefaultConicRhoOrRadius),
            (int)swFeatureFilletProfileType_e.swFeatureFilletConicRhoZeroChamfer => ("ConicRhoZeroChamfer", data.DefaultConicRhoOrRadius),
            _ => throw new InvalidOperationException(
                $"Inspect(Fillet): profile code {code} is not representable on the wire"),
        };
    }

    private static string OverflowToWire(swFilletOverFlowType_e overflow) => overflow switch {
        swFilletOverFlowType_e.swFilletOverFlowType_Default     => "Default",
        swFilletOverFlowType_e.swFilletOverFlowType_KeepEdge    => "KeepEdge",
        swFilletOverFlowType_e.swFilletOverFlowType_KeepSurface => "KeepSurface",
        _ => throw new InvalidOperationException($"Inspect(Fillet): unknown overflow code {(int)overflow}"),
    };

    internal enum WireProfile {
        Circular,
        ConicRho,
        ConicRadius,
        ConicRhoZeroChamfer,
        CurvatureContinuous,
    }

    private enum FaceType { Radius, ChordWidth, HoldLines }

    private sealed record ConstantRadiusArgs(
        IReadOnlyList<Definition> Edges,
        double Radius,
        double? RadiusB,
        bool Symmetric,
        WireProfile Profile,
        double? ConicValue,
        bool TangentPropagation,
        bool RoundCorners,
        swFilletOverFlowType_e Overflow) {

        internal static ConstantRadiusArgs Parse(JsonNode input) {
            var radius = JsonHelpers.ReadDouble(input, "radius");
            if (radius <= 0.0) {
                throw new ArgumentException($"Fillet[ConstantRadius]: radius must be > 0 (got {radius})");
            }
            var radiusB = JsonHelpers.ReadOptionalDouble(input, "radius_b");
            var symmetric = JsonHelpers.ReadBool(input, "symmetric", true);
            if (!symmetric && radiusB is null) {
                throw new ArgumentException("Fillet[ConstantRadius]: symmetric=false requires radius_b to be set");
            }
            if (radiusB is double rb && rb <= 0.0) {
                throw new ArgumentException($"Fillet[ConstantRadius]: radius_b must be > 0 (got {rb})");
            }

            var tangent = JsonHelpers.ReadBool(input, "tangent_propagation", true);
            var profile = ReadProfile(input["profile"]);
            var conic = JsonHelpers.ReadOptionalDouble(input, "conic_value");
            ValidateConic(profile, conic);
            var roundCorners = JsonHelpers.ReadBool(input, "round_corners", false);
            var overflow = ReadOverflow(input["overflow"]);
            var edges = Definition.FromJsonArray(input["edges"], "Fillet.edges");
            return new ConstantRadiusArgs(edges, radius, radiusB, symmetric, profile, conic,
                tangent, roundCorners, overflow);
        }
    }

    private sealed record FaceFilletArgs(
        IReadOnlyList<Definition> FaceSetA,
        IReadOnlyList<Definition> FaceSetB,
        FaceType FaceType,
        double? Radius,
        double? RadiusB,
        bool Symmetric,
        double? ChordWidth,
        IReadOnlyList<Definition> HoldLines,
        WireProfile Profile,
        double? ConicValue,
        Point3D? HelpPoint,
        bool TangentPropagation) {

        internal static FaceFilletArgs Parse(JsonNode input) {
            var setA = Definition.FromJsonArray(input["face_set_a"], "Fillet.face_set_a");
            var setB = Definition.FromJsonArray(input["face_set_b"], "Fillet.face_set_b");
            if (setA.Count == 0) throw new ArgumentException("Fillet[Face]: face_set_a is empty");
            if (setB.Count == 0) throw new ArgumentException("Fillet[Face]: face_set_b is empty");

            var faceType = ReadFaceType(input["face_type"]);
            var profile = ReadProfile(input["profile"]);
            var conic = JsonHelpers.ReadOptionalDouble(input, "conic_value");
            ValidateConic(profile, conic);

            double? radius = JsonHelpers.ReadOptionalDouble(input, "radius");
            double? radiusB = JsonHelpers.ReadOptionalDouble(input, "radius_b");
            var symmetric = JsonHelpers.ReadBool(input, "symmetric", true);
            double? chord = JsonHelpers.ReadOptionalDouble(input, "chord_width");
            var holdLines = input["hold_lines"] is JsonNode hl
                ? Definition.FromJsonArray(hl, "Fillet.hold_lines")
                : (IReadOnlyList<Definition>)Array.Empty<Definition>();

            switch (faceType) {
                case FaceType.Radius:
                    if (radius is null || radius.Value <= 0.0) {
                        throw new ArgumentException("Fillet[Face/Radius]: radius must be > 0");
                    }
                    if (!symmetric && radiusB is null) {
                        throw new ArgumentException("Fillet[Face/Radius]: symmetric=false requires radius_b");
                    }
                    if (radiusB is double rb && rb <= 0.0) {
                        throw new ArgumentException($"Fillet[Face/Radius]: radius_b must be > 0 (got {rb})");
                    }
                    break;
                case FaceType.ChordWidth:
                    if (chord is null || chord.Value <= 0.0) {
                        throw new ArgumentException("Fillet[Face/ChordWidth]: chord_width must be > 0");
                    }
                    break;
                case FaceType.HoldLines:
                    if (holdLines.Count == 0) {
                        throw new ArgumentException("Fillet[Face/HoldLines]: hold_lines is empty");
                    }
                    break;
            }

            var help = ReadHelpPoint(input["help_point"]);
            var tangent = JsonHelpers.ReadBool(input, "tangent_propagation", true);

            return new FaceFilletArgs(setA, setB, faceType, radius, radiusB, symmetric,
                chord, holdLines, profile, conic, help, tangent);
        }
    }

    private sealed record FullRoundArgs(
        IReadOnlyList<Definition> FaceSetA,
        IReadOnlyList<Definition> FaceSetB,
        IReadOnlyList<Definition> FaceSetC,
        bool TangentPropagation) {

        internal static FullRoundArgs Parse(JsonNode input) {
            var setA = Definition.FromJsonArray(input["face_set_a"], "Fillet.face_set_a");
            var setB = Definition.FromJsonArray(input["face_set_b"], "Fillet.face_set_b");
            var setC = Definition.FromJsonArray(input["face_set_c"], "Fillet.face_set_c");
            if (setA.Count == 0) throw new ArgumentException("Fillet[FullRound]: face_set_a is empty");
            if (setB.Count == 0) throw new ArgumentException("Fillet[FullRound]: face_set_b is empty");
            if (setC.Count == 0) throw new ArgumentException("Fillet[FullRound]: face_set_c is empty");
            var tangent = JsonHelpers.ReadBool(input, "tangent_propagation", true);
            return new FullRoundArgs(setA, setB, setC, tangent);
        }
    }

    private static WireProfile ReadProfile(JsonNode? node) {
        var name = node?.GetValue<string>() ?? "Circular";
        return name switch {
            "Circular"            => WireProfile.Circular,
            "ConicRho"            => WireProfile.ConicRho,
            "ConicRadius"         => WireProfile.ConicRadius,
            "ConicRhoZeroChamfer" => WireProfile.ConicRhoZeroChamfer,
            "CurvatureContinuous" => WireProfile.CurvatureContinuous,
            _ => throw new ArgumentException($"Fillet: unknown profile '{name}'"),
        };
    }

    private static void ValidateConic(WireProfile profile, double? conic) {
        switch (profile) {
            case WireProfile.ConicRho:
            case WireProfile.ConicRhoZeroChamfer:
                if (conic is null) {
                    throw new ArgumentException(
                        $"Fillet: profile={profile} requires conic_value (rho ratio in 0.05..0.95)");
                }
                if (conic.Value < 0.05 || conic.Value > 0.95) {
                    throw new ArgumentException(
                        $"Fillet: conic_value (rho) must be in [0.05, 0.95] (got {conic.Value})");
                }
                break;
            case WireProfile.ConicRadius:
                if (conic is null || conic.Value <= 0.0) {
                    throw new ArgumentException(
                        $"Fillet: profile=ConicRadius requires conic_value (meters, > 0)");
                }
                break;
        }
    }

    private static FaceType ReadFaceType(JsonNode? node) {
        var name = node?.GetValue<string>() ?? "Radius";
        return name switch {
            "Radius"     => FaceType.Radius,
            "ChordWidth" => FaceType.ChordWidth,
            "HoldLines"  => FaceType.HoldLines,
            _ => throw new ArgumentException($"Fillet[Face]: unknown face_type '{name}'"),
        };
    }

    private static swFilletOverFlowType_e ReadOverflow(JsonNode? node) {
        var name = node?.GetValue<string>() ?? "Default";
        return name switch {
            "Default"     => swFilletOverFlowType_e.swFilletOverFlowType_Default,
            "KeepEdge"    => swFilletOverFlowType_e.swFilletOverFlowType_KeepEdge,
            "KeepSurface" => swFilletOverFlowType_e.swFilletOverFlowType_KeepSurface,
            _ => throw new ArgumentException($"Fillet: unknown overflow '{name}'"),
        };
    }

    private static Point3D? ReadHelpPoint(JsonNode? node) {
        if (node is null) return null;
        if (node is not JsonObject obj) {
            throw new ArgumentException("Fillet[Face]: help_point must be an object {x, y, z}");
        }
        var x = obj["x"]?.GetValue<double>() ?? throw new ArgumentException("Fillet[Face]: help_point.x missing");
        var y = obj["y"]?.GetValue<double>() ?? throw new ArgumentException("Fillet[Face]: help_point.y missing");
        var z = obj["z"]?.GetValue<double>() ?? 0.0;
        return new Point3D(x, y, z);
    }
}
