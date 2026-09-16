using System.Text.Json.Nodes;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Sketches;

// Angle dimensions. Carries direction (Right/Up/Left/Down/None for one-line cardinal anchoring) and
// the Supplementary/Explementary flip bits used to disambiguate the four equivalent angle landings.
internal static class AngleDimensionConstraint {
    private static readonly Dictionary<string, int> DirectionToInt = new(StringComparer.Ordinal) {
        ["None"]  = 4,
        ["Right"] = 0,
        ["Up"]    = 1,
        ["Left"]  = 2,
        ["Down"]  = 3,
    };
    private const int DirectionNone = 4;

    internal static void Add(File file, Sketch sketch, JsonNode node, List<object> resolved) {
        var value = node["value"]?.GetValue<double>();
        SketchHandler.SelectAllForConstraint(file, resolved);

        var directionToken = node["angle_direction"]?.GetValue<string>() ?? "None";
        var directionInt = DirectionToInt.TryGetValue(directionToken, out var d) ? d : DirectionNone;

        // Text position is LOAD-BEARING for an angle dim: AddDimension2 anchors
        // the extension lines at whichever line endpoints are nearest the text
        // and measures the angle whose interior contains it — so the text picks
        // which of the four witness branches SW lands on. Replaying source's
        // CAPTURED text location reproduces its anchoring deterministically.
        // FlipAnglesAndConverge can match value +
        // supplement/explement but CANNOT recover the witness-endpoint anchoring,
        // so a synthesized point that lands in the wrong wedge silently produces
        // a different one of the 4 angles, changing the profile during replay.
        //
        // Prefer the captured `echo_text_location`; fall back to the synthesized
        // wedge-interior point only for fixtures that didn't capture one.
        var (tx, ty, tz) = node["echo_text_location"] is JsonArray
            ? ConstraintHelpers.ResolveTextLocation(file, sketch, node)
            : (ComputeDeterministicAngleTextLocation(file, sketch, resolved, node)
                ?? ConstraintHelpers.ResolveTextLocation(file, sketch, node));

        // SW quirk: AddDimension2 pops a modal value-prompt dialog unless swInputDimValOnCreate is off; restore after.
        var sld = file.SldWorks;
        sld.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
        try {
            DisplayDimension? disp;
            if (directionInt != DirectionNone) {
                disp = file.ModelDoc.Extension.AddDimension(tx, ty, tz, directionInt) as DisplayDimension;
            } else {
                disp = file.ModelDoc.AddDimension2(tx, ty, tz) as DisplayDimension;
            }
            if (disp is null) {
                throw new InvalidOperationException(
                    $"Dimension Angle could not be created at " +
                    $"tx={tx:G6} ty={ty:G6} tz={tz:G6} sketch='{((Feature)sketch).Name}' selectedCount={resolved.Count}");
            }
            var useOppExtTarget = node["use_opp_ext_line"]?.GetValue<bool>() ?? false;
            var clockwiseTarget = node["clockwise"]?.GetValue<bool>() ?? false;

            // Belt-and-suspenders: even replaying source's captured text point,
            // SW may anchor the extension lines to different endpoints than
            // source for edge cases (two lines whose intersection lies outside
            // both segments, etc.). `line_directions` is therefore a *function
            // of* the achieved anchoring, which we can't set directly. So
            // compare the achieved `FindTowards` to source's captured value per
            // line and flip the target useOpp on each mismatch: 1 mismatch =
            // supplement (flip once), 2 = same angle (flips cancel), 0 = no-op.
            // FlipAnglesAndConverge then drives to the branch reproducing
            // source's geometric angle regardless of how SW anchored. (Blind
            // only when FindTowards returns "None" for a degenerate vertex — but
            // the captured text reproduces anchoring deterministically there.)
            var lineDirsNode = node["line_directions"] as JsonArray;
            if (lineDirsNode is not null) {
                var modelToSketchForAdj = sketch.ModelToSketchTransform;
                if (modelToSketchForAdj is not null
                        && disp.GetDimension2(0) is Dimension adjDim
                        && adjDim.ReferencePoints is object[] adjRefs) {
                    var adjRefPoints = adjRefs.Cast<MathPoint>().ToArray();
                    var n = Math.Min(resolved.Count, lineDirsNode.Count);
                    for (var i = 0; i < n; i++) {
                        var srcTowards = lineDirsNode[i]?.GetValue<string>() ?? "None";
                        if (srcTowards == "None") continue;
                        var curTowards = FindTowards(resolved[i], adjRefPoints, modelToSketchForAdj);
                        if (curTowards == "None") continue;
                        if (curTowards != srcTowards) {
                            useOppExtTarget = !useOppExtTarget;
                        }
                    }
                }
            }

            FlipAnglesAndConverge(file, sketch, disp, useOppExtTarget, clockwiseTarget,
                value ?? 0, value.HasValue);

            // Final geometry guard. AddDimension2 picks the witness anchoring from
            // the text point (which we can't set directly), and the (clockwise,
            // useOpp) flips above are computed relative to THAT anchoring — so if
            // SW anchored differently than source (wrong/edge-case text, or refs
            // resolved in a different order), the dim can still pin the wrong one
            // of the 4 angles. Independent of all that, verify the dim actually
            // drove the two lines to source's GEOMETRIC relative angle (computed
            // frame-free from the captured ref coords); if not, cycle Supp/Expl to
            // the branch that does. No-op when already correct (the common case),
            // so it can't disturb a dim that landed right.
            if (value.HasValue) {
                EnsureSourceAngleGeometry(file, sketch, disp, resolved, node, value.Value);
            }
        } finally {
            sld.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, true);
        }
    }

    internal static JsonObject? Read(File file, Sketch sketch, SketchRelation relation, DisplayDimension disp,
                                     HashSet<(string Kind, long Id)> emittedIds) {
        var dim = disp.GetDimension2(0);
        if (dim is null) return null;

        var value = DimensionConstraint.ReadValue(dim);
        // Skip when refs can't be captured (silhouette / use-edge proxies). Empty refs
        // would replay as AddSpecificDimension with selectedCount=0 and crash on Add.
        var refsJson = ConstraintHelpers.ReadRelationRefs(file, relation, sketch, emittedIds);
        if (refsJson is null) return null;
        var modelToSketch = ((ISketch)sketch).ModelToSketchTransform;

        var lineDirs = new JsonArray();
        for (var i = 0; i < refsJson.Count; i++) lineDirs.Add("None");

        var direction = "None"; var clockwise = false; var useOppExt = false;
        var angleParamsOk = false;

        if (dim.ReferencePoints is object[] refPoints && refPoints.Length >= 3) {
            var mathRefs = refPoints.Cast<MathPoint>().ToArray();
            if (TryReadAngleParams(file, disp, mathRefs, value, modelToSketch,
                    out var dir, out var cw, out var opp)) {
                direction = dir; clockwise = cw; useOppExt = opp;
                angleParamsOk = true;
            }
            // SW quirk: arity must match GetEntitiesCount or per-ref dirs misalign — keep defaults if not.
            if (modelToSketch is not null
                    && relation.GetDefinitionEntities2() is object[] defEnts
                    && defEnts.Length == lineDirs.Count) {
                for (var i = 0; i < defEnts.Length; i++) {
                    lineDirs[i] = FindTowards(defEnts[i], mathRefs, modelToSketch);
                }
            }
        }

        // Undefined angle params + value ~0 => the dim is really enforcing Parallel.
        if (!angleParamsOk && Math.Abs(value) < 1e-9) {
            return new JsonObject {
                ["kind"] = ConstraintKind.Parallel.ToString(),
                ["refs"] = refsJson,
                ["value"] = null,
            };
        }

        return new JsonObject {
            ["kind"] = ConstraintKind.Angle.ToString(),
            ["refs"] = refsJson,
            ["value"] = value,
            // SW dimension name ("D1@Sketch1"), addressable via edit_dimension / delete_constraint.
            ["echo_dim_name"] = dim.FullName,
            ["echo_text_location"] = DimensionConstraint.ReadTextLocation(file, sketch, disp),
            ["line_directions"] = lineDirs,
            ["angle_direction"] = direction,
            ["clockwise"] = clockwise,
            ["use_opp_ext_line"] = useOppExt,
        };
    }

    // SW quirk: fresh angle dim lands on one of four equivalents (clockwise x useOppExtLine); apply Supplementary/Explementary toggles to match. Solver flips on SetSystemValue3 spans > ~pi/2, so converge incrementally.
    private static void FlipAnglesAndConverge(File file, Sketch sketch, DisplayDimension disp,
            bool useOppExtTarget, bool clockwiseTarget, double targetValue, bool valueProvided) {
        const double angleIncrement = Math.PI / 4;
        var dim = disp.GetDimension2(0);
        if (dim is null) return;
        var modelToSketch = ((ISketch)sketch).ModelToSketchTransform;
        if (modelToSketch is null) return;

        SldworksLog.Information(
            "FlipAnglesAndConverge target: cw={Cw} useOpp={UseOpp} value={Val} provided={Provided}",
            clockwiseTarget, useOppExtTarget, targetValue, valueProvided);

        // Step 0: initial state straight from AddDimension2.
        {
            var refPoints = ((object[])dim.ReferencePoints).Cast<MathPoint>().ToArray();
            var curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            var ok = TryReadAngleParams(file, disp, refPoints, curVal, modelToSketch,
                out var dir0, out var cw0, out var opp0);
            SldworksLog.Information(
                "  step0 initial: curVal={Val} readOk={Ok} dir={Dir} cw={Cw} useOpp={UseOpp}",
                curVal, ok, dir0, cw0, opp0);
        }

        // Step 1: flip Supplementary if useOppExt mismatches target.
        {
            var refPoints = ((object[])dim.ReferencePoints).Cast<MathPoint>().ToArray();
            var curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            if (TryReadAngleParams(file, disp, refPoints, curVal, modelToSketch,
                    out _, out _, out var useOppExtCur)) {
                SldworksLog.Information("  step1 read: curVal={Val} useOppCur={Cur} useOppTarget={Tgt} flip={Flip}",
                    curVal, useOppExtCur, useOppExtTarget, useOppExtCur != useOppExtTarget);
                if (useOppExtCur != useOppExtTarget) {
                    disp.SupplementaryAngle();
                    var postVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
                    SldworksLog.Information("  step1 after Supp: curVal={Val}", postVal);
                }
            } else {
                SldworksLog.Information("  step1 read FAILED, skipping Supp flip");
            }
        }

        // Step 2: flip Explementary if clockwise mismatches target.
        {
            var refPoints = ((object[])dim.ReferencePoints).Cast<MathPoint>().ToArray();
            var curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            if (TryReadAngleParams(file, disp, refPoints, curVal, modelToSketch,
                    out _, out var clockwiseCur, out var useOppExtCur2)) {
                SldworksLog.Information("  step2 read: curVal={Val} cwCur={Cur} cwTarget={Tgt} useOppCur(now)={Now} flip={Flip}",
                    curVal, clockwiseCur, clockwiseTarget, useOppExtCur2, clockwiseCur != clockwiseTarget);
                if (clockwiseCur != clockwiseTarget) {
                    disp.ExplementaryAngle();
                    var postVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
                    SldworksLog.Information("  step2 after Expl: curVal={Val}", postVal);
                }
            } else {
                SldworksLog.Information("  step2 read FAILED, skipping Expl flip");
            }
        }

        // Step 3: converge value via finite increments.
        if (!valueProvided) return;
        {
            var curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            SldworksLog.Information("  step3 before converge: curVal={Val} target={Tgt}", curVal, targetValue);
            for (var iter = 0; iter < 64 && Math.Round(curVal - targetValue, 9) != 0; iter++) {
                if (curVal > targetValue) {
                    curVal -= angleIncrement;
                    if (curVal < targetValue) curVal = targetValue;
                } else {
                    curVal += angleIncrement;
                    if (curVal > targetValue) curVal = targetValue;
                }
                dim.SetSystemValue3(curVal, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            }
            SldworksLog.Information("  step3 final: curVal={Val}", curVal);
            // Re-read final branch.
            var refPoints = ((object[])dim.ReferencePoints).Cast<MathPoint>().ToArray();
            var ok = TryReadAngleParams(file, disp, refPoints, curVal, modelToSketch,
                out var dirF, out var cwF, out var oppF);
            SldworksLog.Information("  final read: ok={Ok} dir={Dir} cw={Cw} useOpp={UseOpp}", ok, dirF, cwF, oppF);
        }
    }

    private static bool TryReadAngleParams(File file, DisplayDimension disp, MathPoint[] refPoints, double value, MathTransform? modelToSketch,
            out string direction, out bool clockwise, out bool useOppExtLine) {
        direction = "None"; clockwise = true; useOppExtLine = false;
        if (modelToSketch is null) return false;
        const double tol = 1e-8;
        var pa = (double[])refPoints[1].IMultiplyTransform(modelToSketch).ArrayData;
        var pb = (double[])refPoints[0].IMultiplyTransform(modelToSketch).ArrayData;
        var center = (double[])refPoints[2].IMultiplyTransform(modelToSketch).ArrayData;
        var va = new[] { pa[0] - center[0], pa[1] - center[1] };
        var vb = new[] { pb[0] - center[0], pb[1] - center[1] };
        var aZero = Math.Sqrt(va[0] * va[0] + va[1] * va[1]) < tol;
        var bZero = Math.Sqrt(vb[0] * vb[0] + vb[1] * vb[1]) < tol;
        var angleA = MathUtils.NormalizeAngle(Math.Atan2(va[1], va[0]));
        var angleB = MathUtils.NormalizeAngle(Math.Atan2(vb[1], vb[0]));

        if (aZero || bZero) {
            if (aZero) angleA = angleB;
            direction = ClosestCardinal(angleB);
        }

        var cwSpan = MathUtils.NormalizeAngle(angleA - angleB);
        var ccwSpan = MathUtils.NormalizeAngle(2 * Math.PI - cwSpan);

        if (Math.Abs(MinAngleDiff(cwSpan, value)) < tol) {
            clockwise = true;
        } else if (Math.Abs(MinAngleDiff(ccwSpan, value)) < tol) {
            clockwise = false;
        } else {
            useOppExtLine = true;
            var flippedB = MathUtils.NormalizeAngle(angleB + Math.PI);
            cwSpan = MathUtils.NormalizeAngle(angleA - flippedB);
            ccwSpan = MathUtils.NormalizeAngle(flippedB - angleA);
            if (Math.Abs(MinAngleDiff(cwSpan, value)) < tol) {
                clockwise = true;
            } else if (Math.Abs(MinAngleDiff(ccwSpan, value)) < tol) {
                clockwise = false;
            } else {
                return false;
            }
        }
        return true;
    }

    // Synthesize a model-space (tx, ty, tz) text position that puts SW's
    // AddDimension2 on source's angle branch. Deterministic given the
    // resolved entities + line_directions; returns null when we can't
    // compute (non-SketchLine refs, "None" line_directions, parallel lines,
    // missing transforms).
    private static (double tx, double ty, double tz)? ComputeDeterministicAngleTextLocation(
            File file, Sketch sketch, List<object> resolved, JsonNode node) {
        if (resolved.Count != 2) return null;
        if (resolved[0] is not SketchLine la || resolved[1] is not SketchLine lb) return null;
        var lineDirsNode = node["line_directions"] as JsonArray;
        if (lineDirsNode is null || lineDirsNode.Count != 2) return null;
        var dirA = lineDirsNode[0]?.GetValue<string>() ?? "None";
        var dirB = lineDirsNode[1]?.GetValue<string>() ?? "None";
        if (dirA == "None" || dirB == "None") return null;

        var aS = (SketchPoint)la.GetStartPoint2();
        var aE = (SketchPoint)la.GetEndPoint2();
        var bS = (SketchPoint)lb.GetStartPoint2();
        var bE = (SketchPoint)lb.GetEndPoint2();
        // Source-towards endpoint per line.
        var pA = dirA == "TowardsStart" ? new[] { aS.X, aS.Y } : new[] { aE.X, aE.Y };
        var pB = dirB == "TowardsStart" ? new[] { bS.X, bS.Y } : new[] { bE.X, bE.Y };
        // Vertex: infinite-line intersection of (aS,aE) ∩ (bS,bE). Lines may
        // not actually meet within their segments — that's fine, the dim's
        // extension lines extrapolate.
        var v = IntersectLines2D(
            new[] { aS.X, aS.Y }, new[] { aE.X, aE.Y },
            new[] { bS.X, bS.Y }, new[] { bE.X, bE.Y });
        if (v is null) return null;  // parallel
        // Interior of the wedge formed by (v→pA) and (v→pB): centroid of
        // (v, pA, pB) lies between the two rays and is biased toward the
        // chosen endpoints, so AddDimension2 anchors its extension lines
        // there. Pull the centroid toward the vertex by 80% so it stays
        // inside the wedge even when one ray is much shorter than the other.
        var midA = new[] { (v[0] + pA[0]) / 2.0, (v[1] + pA[1]) / 2.0 };
        var midB = new[] { (v[0] + pB[0]) / 2.0, (v[1] + pB[1]) / 2.0 };
        var localTx = (midA[0] + midB[0]) / 2.0;
        var localTy = (midA[1] + midB[1]) / 2.0;

        var inv = sketch.ModelToSketchTransform?.IInverse();
        if (inv is null) return null;
        var mp = (MathPoint)file.MathUtil.CreatePoint(new[] { localTx, localTy, 0.0 });
        var coords = (double[])mp.IMultiplyTransform(inv).ArrayData;
        return (coords[0], coords[1], coords.Length > 2 ? coords[2] : 0);
    }

    // Infinite-line intersection in 2D. Returns null when parallel (cross product near 0).
    private static double[]? IntersectLines2D(double[] p1, double[] p2, double[] p3, double[] p4) {
        var d1x = p2[0] - p1[0]; var d1y = p2[1] - p1[1];
        var d2x = p4[0] - p3[0]; var d2y = p4[1] - p3[1];
        var denom = d1x * d2y - d1y * d2x;
        const double tol = 1e-12;
        if (Math.Abs(denom) < tol) return null;
        var t = ((p3[0] - p1[0]) * d2y - (p3[1] - p1[1]) * d2x) / denom;
        return new[] { p1[0] + t * d1x, p1[1] + t * d1y };
    }

    private static string FindTowards(object entity, MathPoint[] refPoints, MathTransform modelToSketch) {
        // Only sketch-line refs are recoverable from sketch-side data.
        if (entity is not SketchLine line) return "None";
        const double tol = 1e-8;
        // SketchPoint.X/Y are sketch-local; refPoints are model coords (project to compare).
        var s = (SketchPoint)line.GetStartPoint2();
        var e = (SketchPoint)line.GetEndPoint2();
        var start = new[] { s.X, s.Y };
        var end = new[] { e.X, e.Y };
        var center = (double[])refPoints[2].IMultiplyTransform(modelToSketch).ArrayData;
        var centerToStart = new[] { start[0] - center[0], start[1] - center[1] };
        var centerToEnd = new[] { end[0] - center[0], end[1] - center[1] };
        // SW quirk: when one line endpoint coincides with the angle vertex,
        // centerToStart or centerToEnd is ~(0,0). The dot >= 0 gate then flips on
        // float noise — source and target inspect can disagree on `line_directions`
        // for the same geometry. Skip the gate when either vector is degenerate so
        // both sides deterministically fall through to the endpoint-match loop.
        var startLenSq = centerToStart[0] * centerToStart[0] + centerToStart[1] * centerToStart[1];
        var endLenSq = centerToEnd[0] * centerToEnd[0] + centerToEnd[1] * centerToEnd[1];
        // Both endpoints on the same side of center => no "towards" disambiguator.
        var dot = centerToStart[0] * centerToEnd[0] + centerToStart[1] * centerToEnd[1];
        if (startLenSq > tol * tol && endLenSq > tol * tol && dot >= 0) return "None";
        for (var i = 0; i < 2; i++) {
            var rp = (double[])refPoints[i].IMultiplyTransform(modelToSketch).ArrayData;
            if (Math.Abs(rp[0] - start[0]) < tol && Math.Abs(rp[1] - start[1]) < tol) {
                return "TowardsStart";
            }
            if (Math.Abs(rp[0] - end[0]) < tol && Math.Abs(rp[1] - end[1]) < tol) {
                return "TowardsEnd";
            }
        }
        return "None";
    }

    private static string ClosestCardinal(double angle) {
        var q = (int)Math.Round(angle / (Math.PI / 2));
        q = ((q % 4) + 4) % 4;
        return q switch {
            0 => "Right",
            1 => "Up",
            2 => "Left",
            3 => "Down",
            _ => "None",
        };
    }

    private static double MinAngleDiff(double a, double b) {
        var d = Math.Abs(a - b) % (2 * Math.PI);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    private const double AngleIncrement = Math.PI / 4;

    // Drive the dim's displayed value to `targetValue` in finite steps. SW
    // silently re-measures the supplement/explement on a large single jump, so
    // we step by <= PI/4 (same reason FlipAnglesAndConverge converges
    // incrementally — see the note there).
    private static void ConvergeValue(Dimension dim, double targetValue) {
        var curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
        for (var iter = 0; iter < 64 && Math.Round(curVal - targetValue, 9) != 0; iter++) {
            if (curVal > targetValue) {
                curVal -= AngleIncrement;
                if (curVal < targetValue) curVal = targetValue;
            } else {
                curVal += AngleIncrement;
                if (curVal > targetValue) curVal = targetValue;
            }
            dim.SetSystemValue3(curVal, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            curVal = ((double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
        }
    }

    // The directed angle of a line's CANONICAL start->end direction. Frame
    // applies equally to source-ref and live geometry, so the difference of two
    // of these is an anchor-independent measure of the lines' relative
    // orientation — the thing an angle dim is really pinning.
    private static double DirectedLineAngle(SketchLine line) {
        var s = (SketchPoint)line.GetStartPoint2();
        var e = (SketchPoint)line.GetEndPoint2();
        return Math.Atan2(e.Y - s.Y, e.X - s.X);
    }

    private static double? LineDirFromRef(JsonNode? r) {
        if (r?["kind"]?.GetValue<string>() != "sketch_line") return null;
        var s = r["start"]?["p"] as JsonObject;
        var e = r["end"]?["p"] as JsonObject;
        if (s is null || e is null) return null;
        var sx = s["x"]?.GetValue<double>() ?? 0; var sy = s["y"]?.GetValue<double>() ?? 0;
        var ex = e["x"]?.GetValue<double>() ?? 0; var ey = e["y"]?.GetValue<double>() ?? 0;
        return Math.Atan2(ey - sy, ex - sx);
    }

    // Source's intended relative orientation of the two lines, from the captured
    // ref coords. Returns null when the refs aren't two sketch lines (cardinal
    // angle / point refs — no geometric check to make).
    private static double? DirectedAngleFromRefs(JsonNode node) {
        var refs = node["refs"] as JsonArray;
        if (refs is null || refs.Count != 2) return null;
        var d0 = LineDirFromRef(refs[0]);
        var d1 = LineDirFromRef(refs[1]);
        if (d0 is null || d1 is null) return null;
        return MathUtils.NormalizeAngle(d1.Value - d0.Value);
    }

    private static void EnsureSourceAngleGeometry(File file, Sketch sketch, DisplayDimension disp,
            List<object> resolved, JsonNode node, double value) {
        if (resolved.Count != 2) return;
        if (resolved[0] is not SketchLine l0 || resolved[1] is not SketchLine l1) return;
        var phiSrc = DirectedAngleFromRefs(node);
        if (phiSrc is null) return;
        var dim = disp.GetDimension2(0);
        if (dim is null) return;

        // Tolerance is deliberately loose (~0.06deg): solver/float noise in the
        // live line directions is far below it, while a wrong branch differs by a
        // meaningful angle. When the two branches happen to be near-equal (the
        // dim value ~= 90deg, so supplement ~= itself), the geometry is the same
        // either way and the loose tol correctly treats it as already-correct.
        const double tol = 1e-3;
        double Err() => MinAngleDiff(
            MathUtils.NormalizeAngle(DirectedLineAngle(l1) - DirectedLineAngle(l0)),
            phiSrc.Value);

        if (Err() < tol) return;  // dim reproduced source geometry — nothing to do

        // Cycle the four branches: {}, Supp -> {supp}, Expl -> {supp,expl},
        // Supp -> {expl}. Re-converge the value on each (SW re-measures on flip),
        // and stop at the first whose live geometry matches source's. Exactly one
        // branch reproduces phiSrc, so this terminates at the correct one.
        var toggles = new Action[] {
            () => disp.SupplementaryAngle(),
            () => disp.ExplementaryAngle(),
            () => disp.SupplementaryAngle(),
        };
        foreach (var toggle in toggles) {
            toggle();
            ConvergeValue(dim, value);
            if (Err() < tol) return;
        }
        SldworksLog.Warning(
            "Angle dim on sketch '{Sketch}': could not reach source's geometric angle " +
            "(phiSrc={Src:G6}, residual={Err:G6}) after cycling all branches — " +
            "constraint may be under-defined or refs not two lines",
            ((Feature)sketch).Name, phiSrc.Value, Err());
    }
}
