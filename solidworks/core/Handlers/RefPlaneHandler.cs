using System.Text.Json.Nodes;
using Sldworks.Core;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swcommands;

namespace Sldworks.Core.Handlers;

public static class RefPlaneHandler {
    public const string TypeName = "RefPlane";
    public const string SwTypeName = "RefPlane";

    // ---- Add ---------------------------------------------------------------

    private enum RefPlaneConstraintKind {
        Parallel       = 0,
        Perpendicular  = 1,
        Coincident     = 2,
        Distance       = 3,
        Angle          = 4,
        Tangent        = 5,
        ProjectOnto    = 6,
        MidPlane       = 7,
    }

    // SW quirk: InsertRefPlane packs kind + flags into one int per slot. Kind is one-hot in
    // bits 0..7. Bit 8 = Flipped, 9 = OriginOnCurve, 11 = ProjectToNearestLocation,
    // 12 = ParallelToScreen (UI-only), 13 = ReferenceFlip. SW checks bit 8 OR bit 13
    // depending on kind, so we set both together for "flipped".
    private const int FlipBitA                 = 1 << 8;
    private const int FlipBitB                 = 1 << 13;
    private const int OriginOnCurveBit         = 1 << 9;
    private const int ProjectToNearestBit      = 1 << 11;
    private const int ParallelToScreenBit      = 1 << 12;

    public static JsonNode Add(File file, JsonNode input) {
        var args = RefPlaneArgs.Parse(input);

        if (args.Subtype != RefPlaneSubtypeKind.ConstraintBase) {
            throw new InvalidOperationException(
                $"RefPlane.Add: subtype '{args.Subtype}' is not creatable — only 'ConstraintBase' " +
                "can be authored via InsertRefPlane.");
        }

        if (args.References.Count == 0)
            throw new ArgumentException("RefPlane: at least one reference is required");
        if (args.References.Count > 3)
            throw new ArgumentException("RefPlane: at most three references are supported");
        if (args.Constraints.Count != args.References.Count)
            throw new ArgumentException(
                $"RefPlane: constraints length ({args.Constraints.Count}) must match references length ({args.References.Count})");

        // SW quirk: selection set must be empty before InsertRefPlane — leftover hits
        // become extra references.
        file.ModelDoc.ClearSelection2(true);

        // Resolve the references once — they don't depend on the flip choice.
        var lives = new object[args.References.Count];
        for (var i = 0; i < args.References.Count; i++) {
            lives[i] = DefinitionResolver.Resolve(file, args.References[i])
                ?? throw new InvalidOperationException(
                    $"RefPlane: reference[{i}] ({args.References[i].GetType().Name}) did not resolve to a live entity");
        }
        var authoredFlips = new bool[args.References.Count];
        for (var i = 0; i < args.References.Count; i++) authoredFlips[i] = args.PerReferenceFlip(i);
        var expectedNormal = args.ExpectedNormal ?? args.ExpectedAxes?[2];

        // SW quirk: a RefPlane's solved frame isn't deterministic from its feature data. The normal
        // SIGN is pinned by OrientNormalTo via the Flip_Normal command (a 180-about-in-plane-Y). The
        // POSITION/orientation — which tangent side of a curved face, or which way an Angle plane
        // tilts — is selected by the per-reference flip bits (OptionFlip bit8 / OptionReferenceFlip
        // bit13 in the Constraint int). But the flips set at InsertRefPlane CREATION don't stick — the
        // solver overrides them. The lever that DOES stick is rewriting Constraint[i]'s flip bits on
        // the LIVE feature data and re-solving via ModifyDefinition (in place — keeps the name, so
        // downstream name-refs resolve; no delete/recreate).
        //
        // Create with the authored flips and pin the normal; if the captured frame doesn't match,
        // brute-force the other flip combos in place (ApplyFlipsInPlace), re-pinning the normal after
        // each, until it does. Frame match = expected point ON plane (SW chooses its own in-plane
        // origin, so only the normal-component of the origin difference matters) + normal + in-plane X,
        // all with sign.
        var feature = TryInsertRefPlane(file, args, lives, authoredFlips)
            ?? throw new InvalidOperationException(
                "RefPlane.Add: InsertRefPlane failed (incompatible constraints, broken references, " +
                "or under/over-constrained plane)");
        if (expectedNormal is not null) OrientNormalTo(file, feature, expectedNormal);
        if (args.ExpectedAxes is not null
            && !PlaneFrameMatches(feature, args.ExpectedAxes)
            && !TryFlipCombosInPlace(file, feature, args, authoredFlips, expectedNormal)) {
            throw new InvalidOperationException(
                "RefPlane.Add: no flip combo reproduced the expected frame (expected point on plane + " +
                "normal + in-plane X). A reference may not have resolved as captured.");
        }

        SldworksLog.Information("RefPlaneHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    private static int BuildConstraintInt(
        RefPlaneConstraintKind kind,
        bool flip,
        IReadOnlyList<RefPlaneModifier> modifiers) {
        var v = 1 << (int)kind;
        if (flip) {
            // SW checks bit 8 or bit 13 depending on kind — set both.
            v |= FlipBitA | FlipBitB;
        }
        foreach (var m in modifiers) {
            v |= m switch {
                RefPlaneModifier.OriginOnCurve            => OriginOnCurveBit,
                RefPlaneModifier.ProjectToNearestLocation => ProjectToNearestBit,
                _ => throw new InvalidOperationException($"RefPlane: unknown modifier {m}"),
            };
        }
        return v;
    }

    private static double ResolveAngleOrDistance(RefPlaneConstraintKind kind, double? distance, double? angle) {
        return kind switch {
            RefPlaneConstraintKind.Distance => distance
                ?? throw new ArgumentException("RefPlane: 'distance' is required when any constraint is Distance"),
            RefPlaneConstraintKind.Angle => angle
                ?? throw new ArgumentException("RefPlane: 'angle' is required when any constraint is Angle"),
            _ => 0.0,
        };
    }

    // Pin the plane's normal SIGN to the model's stated facing. InsertRefPlane's base normal
    // is nondeterministic (+N/-N) across rebuilds, and the feature-data flip flags are inert
    // post-creation — so the ONLY way to flip a solved normal is the Flip_Normal UI command.
    // Fire it iff the live normal points opposite expected_normal. Deterministic — no search.
    private static void OrientNormalTo(File file, Feature feature, double[] expectedNormal) {
        var normal = ReadCurrentAxes(feature)[2];  // row 2 = plane normal
        var dot = MathUtils.DotProduct(normal, expectedNormal);
        if (dot >= 0) {
            SldworksLog.Information(
                "RefPlane.Add orient: {Name} normal already faces expected_normal (dot={Dot:G7})",
                feature.Name, dot);
            return;
        }

        // 180° about the in-plane Y: flips the normal (and X — compensated downstream).
        if (!FlipNormalViaCommand(file, feature)) {
            throw new InvalidOperationException(
                $"RefPlane.Add: Flip_Normal command failed to select/flip {feature.Name}");
        }

        var after = MathUtils.DotProduct(ReadCurrentAxes(feature)[2], expectedNormal);
        SldworksLog.Information(
            "RefPlane.Add orient: {Name} flipped via Flip_Normal command (dot {Before:G7} -> {After:G7})",
            feature.Name, dot, after);
        if (after < 0) {
            throw new InvalidOperationException(
                $"RefPlane.Add: Flip_Normal did not align the normal with expected_normal (dot still {after:G7})");
        }
    }

    // ---- Full-frame verification (RTT axes match) --------------------------

    // True iff the plane matches GEOMETRICALLY: the expected point lies ON the plane (only the
    // normal-component of the origin difference matters — SW parks its origin anywhere in-plane) AND
    // the normal matches WITH SIGN (pinned by orient). A point + normal fully determine a plane, so
    // that's all correctness needs. The in-plane X basis (xd) is logged for diagnostics but NOT
    // required: SW can solve a plane to the right normal+position with a MIRRORED in-plane X (an Angle
    // plane) and there's no reliable lever to unflip it; it's the same plane, and only
    // expected_normal is diffed downstream (never the X row), so tolerating it is correct.
    private static bool PlaneFrameMatches(Feature feature, double[][] expectedAxes) {
        var actual = ReadCurrentAxes(feature);   // [X, Y, normal, origin]
        var n = actual[2]; var ao = actual[3]; var eo = expectedAxes[3];
        var nlen = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        var od = nlen > 0
            ? Math.Abs(((eo[0] - ao[0]) * n[0] + (eo[1] - ao[1]) * n[1] + (eo[2] - ao[2]) * n[2]) / nlen)
            : double.PositiveInfinity;
        var nd = Math.Abs(1.0 - MathUtils.DotProduct(expectedAxes[2], actual[2]));   // normal, with sign
        var xd = Math.Abs(1.0 - MathUtils.DotProduct(expectedAxes[0], actual[0]));   // in-plane X, with sign
        SldworksLog.Information(
            "RefPlane.Add frame-check: {Name} pointOffPlane={Od:G6} nd={Nd:G6} xd={Xd:G6} normal={Normal} X={X}",
            feature.Name, od, nd, xd, FormatVec(actual[2]), FormatVec(actual[0]));
        return od < Flags.GeometryTolerance && nd < Flags.GeometryTolerance;
    }

    // Build + insert a RefPlane for the authored flip combo. References resolved by the caller; this
    // re-selects them at their marks (InsertRefPlane consumes the selection set). Unused
    // slots = 0; the interop signature is `short` (COM INT16).
    private static Feature? TryInsertRefPlane(File file, RefPlaneArgs args, object[] lives, bool[] flips) {
        file.ModelDoc.ClearSelection2(true);
        var constraintInts = new int[3];
        var angleOrDistances = new double[3];
        for (var i = 0; i < args.References.Count; i++) {
            SelectAtMark(file, lives[i], i);
            constraintInts[i] = BuildConstraintInt(args.Constraints[i], flips[i], args.PerReferenceModifiers(i));
            angleOrDistances[i] = ResolveAngleOrDistance(args.Constraints[i], args.Distance, args.Angle);
        }
        var fm = file.ModelDoc.FeatureManager;
        return fm.InsertRefPlane(
            (short)constraintInts[0], angleOrDistances[0],
            (short)constraintInts[1], angleOrDistances[1],
            (short)constraintInts[2], angleOrDistances[2]) as Feature;
    }

    // Candidate per-reference flip combos: authored first (the common case lands there), then every
    // other combination, so an ambiguity the in-place SolutionIndex sweep can't reach (e.g. an Angle
    // plane's tilt direction) is recovered by recreating with the opposite flip bits.
    private static IEnumerable<bool[]> FlipCandidates(bool[] authored) {
        yield return (bool[])authored.Clone();
        var n = authored.Length;
        for (var mask = 0; mask < (1 << n); mask++) {
            var combo = new bool[n];
            var differs = false;
            for (var i = 0; i < n; i++) {
                combo[i] = (mask & (1 << i)) != 0;
                if (combo[i] != authored[i]) differs = true;
            }
            if (differs) yield return combo;
        }
    }

    // Brute-force the per-reference flip flags IN PLACE. For each combo that differs from the authored
    // one, rewrite the live feature's Constraint[i] flip bits and re-solve via ModifyDefinition — which
    // moves the plane to the other tangent side / angle-tilt direction (the flips set at InsertRefPlane
    // creation don't stick, but editing them on the BUILT feature does). Re-pin the normal sign after
    // each re-solve, then check the captured frame. In place, so the feature keeps its name — downstream
    // name-refs resolve without any delete/recreate/rename.
    private static bool TryFlipCombosInPlace(File file, Feature feature, RefPlaneArgs args,
            bool[] authoredFlips, double[]? expectedNormal) {
        if (args.ExpectedAxes is null) return false;
        foreach (var flips in FlipCandidates(authoredFlips)) {
            if (FlipsEqual(flips, authoredFlips)) continue;  // authored combo already created + checked
            if (!ApplyFlipsInPlace(file, feature, args, flips)) continue;
            if (expectedNormal is not null) OrientNormalTo(file, feature, expectedNormal);
            if (PlaneFrameMatches(feature, args.ExpectedAxes)) {
                SldworksLog.Information(
                    "RefPlane.Add: matched in place via flip combo [{Flips}] (ModifyDefinition)",
                    string.Join(",", flips));
                return true;
            }
        }
        return false;
    }

    // Rewrite the live plane's per-reference flip bits (Constraint[i]) and re-solve via ModifyDefinition.
    // IAccessSelections loads the reference selections so ModifyDefinition can re-apply them; it releases
    // on success, so we only release on the failure/throw paths. Returns true iff the re-solve applied.
    private static bool ApplyFlipsInPlace(File file, Feature feature, RefPlaneArgs args, bool[] flips) {
        if (feature.GetDefinition() is not IRefPlaneFeatureData data) return false;
        if (!data.IAccessSelections(file.ModelDoc, null)) return false;
        bool applied;
        try {
            for (var i = 0; i < args.References.Count; i++) {
                data.Constraint[i] = BuildConstraintInt(args.Constraints[i], flips[i], args.PerReferenceModifiers(i));
            }
            applied = feature.ModifyDefinition(data, file.ModelDoc, null);
        } catch {
            try { data.ReleaseSelectionAccess(); } catch { /* may already be released */ }
            throw;
        }
        if (!applied) { try { data.ReleaseSelectionAccess(); } catch { /* may already be released */ } }
        return applied;
    }

    private static bool FlipsEqual(bool[] a, bool[] b) {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string FormatVec(double[] v) =>
        $"({string.Join(", ", v.Select(x => x.ToString("G7", System.Globalization.CultureInfo.InvariantCulture)))})";

    private static double[][] ReadCurrentAxes(Feature feature) {
        // SW quirk: GetSpecificFeature2() for a RefPlane casts to
        // SolidWorks.Interop.sldworks.RefPlane (NOT a Feature).
        var spec = feature.GetSpecificFeature2() as SolidWorks.Interop.sldworks.RefPlane
            ?? throw new InvalidOperationException(
                "RefPlane.Add: GetSpecificFeature2 did not return a RefPlane; cannot read PlaneAxes");
        return MathUtils.GetTransformMatrix(spec.Transform.IInverse());
    }

    private static JsonArray AxesToJson(double[][] axes) {
        var jsonAxes = new JsonArray();
        foreach (var row in axes) {
            jsonAxes.Add(new JsonArray(row[0], row[1], row[2]));
        }
        return jsonAxes;
    }

    // Flip the plane's SOLVED normal via the UI command (swCommands_RefPlane_Flip_Normal = 3113).
    // This acts on SW's solved geometry — the only lever that actually moves the normal (the
    // feature-data flags don't). Requires a clean selection set and NO held IAccessSelections.
    private static bool FlipNormalViaCommand(File file, Feature feature) {
        file.ModelDoc.ClearSelection2(true);
        if (!feature.Select2(false, 0)) return false;
        file.ModelDoc.Extension.RunCommand((int)swCommands_e.swCommands_RefPlane_Flip_Normal, "");
        file.ModelDoc.ClearSelection2(true);
        return true;
    }

    // ---- Edit --------------------------------------------------------------

    // Edit an existing RefPlane IN PLACE (GetDefinition -> IAccessSelections -> mutate ->
    // ModifyDefinition). We do NOT delete + re-add, so the feature name — and every downstream
    // name-ref / probe — survives.
    //
    // TWO things are editable, both matching what Add authors:
    //   1. The offset SCALAR — the Distance value of a Distance-offset plane, or the Angle value
    //      of an Angle-offset plane (the only planes that carry a scalar).
    //   2. The reference SELECTIONS — the entities (faces/planes/edges/points) the plane is
    //      constrained to, via the per-slot Reference[i] property. Re-pointed only when the
    //      incoming refs DIFFER from the built ones AND the per-slot CONSTRAINT SHAPE is
    //      unchanged (same kinds, same count, same order). Re-pointing onto different geometry of
    //      the SAME constraint shape is in scope; a different constraint combo is a different
    //      construction (out of scope — see EXCLUSIONS below).
    //
    // We deliberately DO NOT touch:
    //   - the per-slot flip bits / ReversedReferenceDirection (that lever lives in
    //     ApplyFlipsInPlace, Add's frame-reproduction machinery) — kept as built. After a
    //     re-point we DO re-pin the solved normal SIGN to expected_normal via the same
    //     Flip_Normal command Add uses post-build, because a new reference can solve to the
    //     opposite normal; that is the only orientation lever applied, and only when the wire
    //     supplies expected_normal.
    //   - the Constraint[i] KIND bits (a kind change is a different construction).
    //
    // EXCLUSIONS (clear throws — delete and re-add):
    //   - A constraint combo the wire/API can't encode in place: the payload's per-slot
    //     constraint kinds/count/order differ from the built plane's (RepointReferencesIfChanged
    //     refuses), or a non-ConstraintBase leaf subtype was authored with references to re-point
    //     (the leaf Distance/Angle subtypes are Inspect-only constructions; their per-slot kind
    //     layout isn't on the wire to validate against).
    //
    // Reuses the IAccessSelections -> ModifyDefinition lifecycle from ApplyFlipsInPlace.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var data = feature.GetDefinition() as IRefPlaneFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IRefPlaneFeatureData");

        // Decide which scalar (if any) this plane exposes, from its OWN built kind — not from the
        // input. A ConstraintBase plane carries its kind in the Constraint[i] bits; the
        // Distance/Angle leaf subtypes (swRefPlaneDistance / swRefPlaneAngle) carry it in Type2.
        var subtype = (swRefPlaneType_e)data.Type2;
        if (subtype is swRefPlaneType_e.swRefPlaneInvalid
                    or swRefPlaneType_e.swRefPlaneUndefined) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: feature {feature.Name} has Type2={subtype} (an error state); "
                + "the plane did not resolve to a usable subtype.");
        }

        // The expected normal (if the wire supplies it) re-pins the solved normal sign after a
        // re-point — Add's post-build OrientNormalTo, applied here for the same reason.
        var expectedNormal = ParseExpectedNormalForEdit(input);

        // ConstraintBase planes carry re-pointable references on the wire (per-slot Reference[i]
        // + Constraint[i]); the leaf Distance/Angle subtypes are Inspect-only constructions with
        // no per-slot constraint layout on the wire, so they remain offset-scalar-only.
        if (subtype == swRefPlaneType_e.swRefPlaneConstraintBase) {
            return EditConstraintBase(file, feature, data, input, expectedNormal);
        }
        return EditLeafOffset(file, feature, data, subtype, input);
    }

    // ConstraintBase edit: re-point references (guarded) + set the offset scalar (if any), then
    // ModifyDefinition, release, and re-pin the normal sign.
    private static JsonNode EditConstraintBase(
            File file, Feature feature, IRefPlaneFeatureData data, JsonNode input, double[]? expectedNormal) {
        // Classify the offset kind (if any) from the BUILT plane. A ConstraintBase plane may have
        // NO scalar (coincident/parallel/midplane/...); that's fine now — references are still
        // re-pointable. Only when a Distance/Angle slot exists do we require + read its value.
        var offsetKind = ClassifyConstraintBaseOffset(file, feature, data);
        double? newValue = offsetKind switch {
            RefPlaneConstraintKind.Distance => ReadRequired(input, "distance", feature.Name, "Distance"),
            RefPlaneConstraintKind.Angle    => ReadRequired(input, "angle", feature.Name, "Angle"),
            _ => (double?)null,
        };

        // SW quirk: IAccessSelections is required before mutating feature data + must be paired
        // with ReleaseSelectionAccess on EVERY exit path (mirrors Inspect / ApplyFlipsInPlace),
        // or the document wedges. ModifyDefinition re-applies the reference selections it loaded.
        if (!data.IAccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: IAccessSelections failed for {feature.Name}");
        }
        var repointed = false;
        try {
            // RE-POINT references first (guarded: only changed slots, only when the constraint
            // shape is unchanged), then set the offset value on its slot.
            repointed = RepointReferencesIfChanged(file, data, input, feature.Name);

            if (newValue is double v) {
                // ConstraintBase keeps its value in the per-slot AngleOrDistance[]; set the slot
                // that carries the Distance/Angle constraint. Constraint[i] kind bits untouched.
                // offsetKind is non-null here: newValue is set iff offsetKind is Distance/Angle.
                var slot = FindOffsetSlot(data, offsetKind!.Value, feature.Name);
                data.AngleOrDistance[slot] = v;
            }

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"RefPlane.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "an offset or re-pointed reference is invalid against the geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // SW quirk: Flip_Normal needs a clean selection set and NO held IAccessSelections, so it
        // runs after the release. A new reference can solve to the opposite normal; re-pin the
        // sign to the model's stated facing exactly as Add does post-build. Only when a re-point
        // actually happened (an unchanged plane keeps its solved normal) and the wire supplied it.
        if (repointed && expectedNormal is not null) {
            OrientNormalTo(file, feature, expectedNormal);
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT AfterFeature:
        // a mid-tree plane must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "RefPlaneHandler.Edit: {Name} offsetKind={Kind} value={Value} repointed={Repointed}",
            feature.Name, offsetKind, newValue?.ToString("G7") ?? "(none)", repointed);
        return Inspect(file, feature);
    }

    // Leaf Distance/Angle subtype edit: offset-scalar-only (no per-slot references on the wire).
    private static JsonNode EditLeafOffset(
            File file, Feature feature, IRefPlaneFeatureData data, swRefPlaneType_e subtype, JsonNode input) {
        var offsetKind = subtype == swRefPlaneType_e.swRefPlaneDistance
            ? RefPlaneConstraintKind.Distance
            : subtype == swRefPlaneType_e.swRefPlaneAngle
                ? RefPlaneConstraintKind.Angle
                : throw new InvalidOperationException(
                    $"RefPlane.Edit: only Distance/Angle offset planes are editable in place — "
                    + $"'{feature.Name}' is subtype {SubtypeToWire(subtype)}; delete and re-add.");

        double newValue = offsetKind == RefPlaneConstraintKind.Distance
            ? ReadRequired(input, "distance", feature.Name, "Distance")
            : ReadRequired(input, "angle", feature.Name, "Angle");

        if (!data.IAccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: IAccessSelections failed for {feature.Name}");
        }
        try {
            if (offsetKind == RefPlaneConstraintKind.Distance) data.Distance = newValue;
            else data.Angle = newValue;

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"RefPlane.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + $"{(offsetKind == RefPlaneConstraintKind.Angle ? "angle" : "distance")} invalid "
                    + "against the reference geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information(
            "RefPlaneHandler.Edit: {Name} set {Kind}={Value:G7}", feature.Name, offsetKind, newValue);
        return Inspect(file, feature);
    }

    // Classify a ConstraintBase plane's offset kind (Distance / Angle / None) from its built
    // Constraint[i] bits. Opens its OWN IAccessSelections scope (paired release). Returns null
    // when the plane carries no editable scalar (coincident/parallel/midplane/...).
    private static RefPlaneConstraintKind? ClassifyConstraintBaseOffset(
            File file, Feature feature, IRefPlaneFeatureData data) {
        if (!data.IAccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: IAccessSelections failed for {feature.Name}");
        }
        try {
            var hasDistance = false;
            var hasAngle = false;
            for (var i = 0; i < 3; i++) {
                var constraintInt = data.Constraint[i];
                if (constraintInt == 0) continue;
                var kind = DecodeConstraintKind(constraintInt);
                if (kind == RefPlaneConstraintKind.Distance) hasDistance = true;
                else if (kind == RefPlaneConstraintKind.Angle) hasAngle = true;
            }
            // A plane carries at most one offset scalar in our schema (Add resolves a single
            // distance/angle). Distance and Angle are mutually exclusive offset kinds.
            if (hasDistance && hasAngle) {
                throw new InvalidOperationException(
                    $"RefPlane.Edit: '{feature.Name}' has both Distance and Angle constraints; "
                    + "ambiguous offset — delete and re-add.");
            }
            if (hasDistance) return RefPlaneConstraintKind.Distance;
            if (hasAngle) return RefPlaneConstraintKind.Angle;
            return null;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // Re-point the reference entities a ConstraintBase plane is constrained to, in place. The
    // incoming refs arrive in the Edit payload (`references`, the SAME defs Add resolves+selects);
    // here — unlike Add (select-then-InsertRefPlane) — we resolve each to a live entity and set
    // the per-slot Reference[i] property directly, inside the open IAccessSelections block, before
    // ModifyDefinition — unconditionally (assume the payload is authoritative; re-applying the same
    // reference is idempotent, so the common scalar-only edit is unaffected). Returns true iff the
    // payload supplied references (so the caller knows to re-pin the solved normal).
    //
    // SHAPE CHECK (kept — a documented hard EXCLUSION, not a guard): the payload's per-slot
    // constraint kinds must match the built plane's exactly — same kinds, same count, same order.
    // Re-pointing onto different geometry of the SAME constraint shape is in scope; a different
    // combo is a different construction (throws — delete and re-add).
    private static bool RepointReferencesIfChanged(
            File file, IRefPlaneFeatureData data, JsonNode input, string featureName) {
        // The Edit payload may omit references entirely (a pure scalar edit). Treat missing as
        // "the field wasn't provided" — keep references as built.
        if (input["references"] is not JsonArray refsNode) return false;

        var requestedRefs = ParseReferences(refsNode);
        var requestedKinds = ParseConstraints(input["constraints"]);
        if (requestedRefs.Count == 0) return false;  // field not provided — nothing to apply
        if (requestedKinds.Count != requestedRefs.Count) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: '{featureName}' constraints length ({requestedKinds.Count}) must match "
                + $"references length ({requestedRefs.Count}).");
        }

        // Built layout: the occupied slots in order (constraint kind + slot index). Reference[i]
        // is meaningful only where Constraint[i] != 0.
        var builtSlots = new List<(int Slot, RefPlaneConstraintKind Kind)>();
        for (var i = 0; i < 3; i++) {
            var constraintInt = data.Constraint[i];
            if (constraintInt == 0) continue;
            var kind = DecodeConstraintKind(constraintInt)
                ?? throw new InvalidOperationException(
                    $"RefPlane.Edit: '{featureName}' slot {i} has unknown constraint bits {constraintInt}.");
            builtSlots.Add((i, kind));
        }

        // SHAPE CHECK: same number of constrained references, and same constraint kind in the same
        // order. A mismatch means a different construction the in-place edit can't encode.
        if (builtSlots.Count != requestedRefs.Count) {
            throw new InvalidOperationException(
                $"RefPlane.Edit: '{featureName}' has {builtSlots.Count} constrained reference(s) but the "
                + $"payload supplies {requestedRefs.Count}; changing the reference COUNT is a different "
                + "construction — delete and re-add.");
        }
        for (var k = 0; k < builtSlots.Count; k++) {
            if (builtSlots[k].Kind != requestedKinds[k]) {
                throw new InvalidOperationException(
                    $"RefPlane.Edit: '{featureName}' slot {builtSlots[k].Slot} constraint "
                    + $"{builtSlots[k].Kind} -> {requestedKinds[k]} is a different construction — delete and re-add.");
            }
        }

        // Resolve + set each slot's incoming reference unconditionally.
        for (var k = 0; k < builtSlots.Count; k++) {
            var slot = builtSlots[k].Slot;

            var live = DefinitionResolver.Resolve(file, requestedRefs[k])
                ?? throw new InvalidOperationException(
                    $"RefPlane.Edit: references[{k}] ({requestedRefs[k].GetType().Name}) did not resolve "
                    + $"to a live entity on '{featureName}'.");

            // SW quirk: Reference is an indexed scalar dispatch property — set_Reference(int, Object)
            // takes ONE live entity per slot (NOT a SAFEARRAY), so the bare resolved COM object is
            // assigned directly, no DispatchWrapper (mirrors Constraint[i] / AngleOrDistance[i] and
            // Chamfer's scalar IVertex). The Constraint[i] kind+flip bits stay as built.
            data.Reference[slot] = live;
            SldworksLog.Information(
                "RefPlaneHandler.Edit: applied reference slot {Slot} of '{Name}'", slot, featureName);
        }
        return true;
    }

    // expected_normal for the post-re-point normal re-pin (Add's OrientNormalTo). Optional —
    // null when the wire omits it (then the solved normal is left as ModifyDefinition produced it).
    private static double[]? ParseExpectedNormalForEdit(JsonNode input) =>
        ParseExpectedNormal(input["expected_normal"]);

    // Index of the Constraint[] slot carrying the Distance/Angle offset. Caller holds
    // IAccessSelections.
    private static int FindOffsetSlot(IRefPlaneFeatureData data, RefPlaneConstraintKind offsetKind, string name) {
        for (var i = 0; i < 3; i++) {
            var constraintInt = data.Constraint[i];
            if (constraintInt == 0) continue;
            if (DecodeConstraintKind(constraintInt) == offsetKind) return i;
        }
        throw new InvalidOperationException(
            $"RefPlane.Edit: no {offsetKind} constraint slot found on '{name}' (lost between classify and set?)");
    }

    private static double ReadRequired(JsonNode input, string field, string name, string offsetKindLabel) {
        var v = input[field]
            ?? throw new ArgumentException(
                $"RefPlane.Edit: '{name}' is a {offsetKindLabel} offset plane — '{field}' is required");
        return v.GetValue<double>();
    }

    // ---- Inspect -----------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as IRefPlaneFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IRefPlaneFeatureData");

        var subtype = (swRefPlaneType_e)data.Type2;
        if (subtype is swRefPlaneType_e.swRefPlaneInvalid
                    or swRefPlaneType_e.swRefPlaneUndefined) {
            throw new InvalidOperationException(
                $"RefPlane.Inspect: feature {feature.Name} has Type2={subtype} (an error state); " +
                "the plane did not resolve to a usable subtype.");
        }

        if (subtype != swRefPlaneType_e.swRefPlaneConstraintBase) {
            return InspectNonConstraintBase(file, feature, subtype);
        }

        // SW quirk: IAccessSelections is required before reading Reference[i] / Constraint[i],
        // and ReleaseSelectionAccess MUST run on every exit path or the document wedges.
        if (!data.IAccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException($"RefPlane.Inspect: IAccessSelections failed for {feature.Name}");
        }

        try {
            var references = new JsonArray();
            var constraints = new JsonArray();
            var flips = new JsonArray();
            var modifiers = new JsonArray();
            var anyFlip = false;
            var anyModifier = false;
            double? distance = null;
            double? angle = null;

            // SW quirk: Constraint[]/Reference[]/AngleOrDistance[] are fixed length 3
            // with unused slots = 0 / null.
            for (var i = 0; i < 3; i++) {
                var constraintInt = data.Constraint[i];
                if (constraintInt == 0) continue;

                var refObj = data.Reference[i]
                    ?? throw new InvalidOperationException(
                        $"RefPlane.Inspect: reference[{i}] is null on {feature.Name} but constraint bits are set");

                if ((constraintInt & ParallelToScreenBit) != 0) {
                    throw new InvalidOperationException(
                        $"RefPlane.Inspect: ParallelToScreen constraint on slot {i} of {feature.Name} is not representable on the wire");
                }

                var kind = DecodeConstraintKind(constraintInt)
                    ?? throw new InvalidOperationException(
                        $"RefPlane.Inspect: unknown constraint bits {constraintInt} (slot {i}) on {feature.Name}");

                references.Add(DefinitionCapture.Capture(refObj)?.ToJson()
                    ?? throw new InvalidOperationException(
                        $"RefPlane.Inspect: reference is unsupported runtime type {refObj.GetType().Name}"));
                constraints.Add(kind.ToString());

                // SW sets bit 8 and/or bit 13 for "flipped" — report flipped if either set.
                var slotFlip = (constraintInt & (FlipBitA | FlipBitB)) != 0;
                flips.Add(slotFlip);
                if (slotFlip) anyFlip = true;

                var slotModifiers = new JsonArray();
                if ((constraintInt & OriginOnCurveBit) != 0) {
                    slotModifiers.Add("OriginOnCurve");
                    anyModifier = true;
                }
                if ((constraintInt & ProjectToNearestBit) != 0) {
                    slotModifiers.Add("ProjectToNearestLocation");
                    anyModifier = true;
                }
                modifiers.Add(slotModifiers);

                var v = data.AngleOrDistance[i];
                if (kind == RefPlaneConstraintKind.Distance) distance ??= v;
                else if (kind == RefPlaneConstraintKind.Angle) angle ??= v;
            }

            var result = new JsonObject {
                ["type"] = TypeName,
                ["subtype"] = "ConstraintBase",
                ["name"] = feature.Name,
                ["references"] = references,
                ["constraints"] = constraints,
            };
            if (anyFlip) result["flips"] = flips;
            if (anyModifier) result["modifiers"] = modifiers;
            if (distance is not null) result["distance"] = distance;
            if (angle is not null)    result["angle"] = angle;
            // `expected_normal` (row 2 = unit normal) is the low-cardinality, model-facing
            // orientation input — it IS diffed, and it's what the Add's Flip_Normal decision
            // keys on. `expected_axes` (full frame) is the RTT correctness check: the Add
            // verifies origin + normal + in-plane X all land after the command flip. `flips`
            // (per-reference, consumed at creation) + `flip_normal` are informational replay
            // inputs, NOT diffed.
            var axes = ReadCurrentAxes(feature);
            result["expected_normal"] = new JsonArray(axes[2][0], axes[2][1], axes[2][2]);
            result["expected_axes"] = AxesToJson(axes);
            result["flip_normal"] = data.ReverseDirection;
            SldworksLog.Information(
                "RefPlane.Inspect: {Name} expected_normal=({Nx:G7},{Ny:G7},{Nz:G7}) reverse={Rev} any_flip={AnyFlip}",
                feature.Name, axes[2][0], axes[2][1], axes[2][2], data.ReverseDirection, anyFlip);
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }


    // Non-ConstraintBase subtypes leave `constraints` empty because the per-slot bit layout differs from InsertRefPlane's kind bits.
    private static JsonNode InspectNonConstraintBase(File file, Feature feature, swRefPlaneType_e subtype) {
        var data = (IRefPlaneFeatureData)feature.GetDefinition();
        var accessOk = data.IAccessSelections(file.ModelDoc, null);
        try {
            var references = new JsonArray();
            if (accessOk) {
                for (var i = 0; i < 3; i++) {
                    var refObj = data.Reference[i];
                    if (refObj is null) continue;
                    references.Add(DefinitionCapture.Capture(refObj)?.ToJson()
                    ?? throw new InvalidOperationException(
                        $"RefPlane.Inspect: reference is unsupported runtime type {refObj.GetType().Name}"));
                }
            }

            var axes = ReadCurrentAxes(feature);
            var result = new JsonObject {
                ["type"] = TypeName,
                ["subtype"] = SubtypeToWire(subtype),
                ["name"] = feature.Name,
                ["references"] = references,
                ["constraints"] = new JsonArray(),
                ["expected_normal"] = new JsonArray(axes[2][0], axes[2][1], axes[2][2]),
                ["expected_axes"] = AxesToJson(axes),
                ["flip_normal"] = data.ReverseDirection,
            };
            if (subtype == swRefPlaneType_e.swRefPlaneDistance) {
                result["distance"] = data.Distance;
            } else if (subtype == swRefPlaneType_e.swRefPlaneAngle) {
                result["angle"] = data.Angle;
            }
            return result;
        } finally {
            if (accessOk) {
                data.ReleaseSelectionAccess();
            }
        }
    }

    private static string SubtypeToWire(swRefPlaneType_e subtype) => subtype switch {
        swRefPlaneType_e.swRefPlaneConstraintBase => "ConstraintBase",
        swRefPlaneType_e.swRefPlaneLinePoint      => "LinePoint",
        swRefPlaneType_e.swRefPlaneThreePoint     => "ThreePoint",
        swRefPlaneType_e.swRefPlaneLineLine       => "LineLine",
        swRefPlaneType_e.swRefPlaneDistance       => "Distance",
        swRefPlaneType_e.swRefPlaneParallel       => "Parallel",
        swRefPlaneType_e.swRefPlaneAngle          => "Angle",
        swRefPlaneType_e.swRefPlaneNormal         => "Normal",
        swRefPlaneType_e.swRefPlaneOnSurface      => "OnSurface",
        swRefPlaneType_e.swRefPlaneSWStandard     => "SWStandard",
        _ => throw new InvalidOperationException(
            $"RefPlane.Inspect: unknown swRefPlaneType_e value {subtype} ({(int)subtype})"),
    };

    private static RefPlaneConstraintKind? DecodeConstraintKind(int bits) {
        // Kind is a single bit in 0..7; pick the lowest so modifier bits don't pollute it.
        for (var i = 0; i <= 7; i++) {
            if ((bits & (1 << i)) != 0) return (RefPlaneConstraintKind)i;
        }
        return null;
    }

    // ---- Selection helpers -------------------------------------------------

    private static void SelectAtMark(File file, object live, int mark) {
        // Delegate to Definition.SelectLive so per-runtime-type quirks (Edge/Face2/Vertex via
        // IEntity, SketchSegment/SketchPoint direct, Body2.Select2, Feature.Select2 with bare int)
        // stay in one place. SketchPoint is a valid coincident-with-point constraint reference.
        if (!Definition.SelectLive(file, live, mark)) {
            throw new InvalidOperationException(
                $"RefPlane: SW rejected selection of {live.GetType().Name} on mark {mark}");
        }
    }

    // ---- Result shape ------------------------------------------------------

    private static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Typed args --------------------------------------------------------

    private enum RefPlaneModifier {
        OriginOnCurve,
        ProjectToNearestLocation,
    }

    private enum RefPlaneSubtypeKind {
        ConstraintBase,
        LinePoint,
        ThreePoint,
        LineLine,
        Distance,
        Parallel,
        Angle,
        Normal,
        OnSurface,
        SWStandard,
    }

    private sealed record RefPlaneArgs(
        RefPlaneSubtypeKind Subtype,
        List<Definition> References,
        List<RefPlaneConstraintKind> Constraints,
        List<bool> Flips,
        List<List<RefPlaneModifier>> Modifiers,
        double? Distance,
        double? Angle,
        bool FlipNormal,
        double[][]? ExpectedAxes,
        double[]? ExpectedNormal,
        string? Name) {

        private static readonly IReadOnlyList<RefPlaneModifier> Empty = Array.Empty<RefPlaneModifier>();

        internal static RefPlaneArgs Parse(JsonNode input) {
            var subtype = ParseSubtype(input["subtype"]);
            var refs = ParseReferences(input["references"]);
            var constraints = ParseConstraints(input["constraints"]);
            var flips = ParseFlips(input["flips"], refs.Count);
            var modifiers = ParseModifiers(input["modifiers"], refs.Count);
            var distance = ReadOptionalDouble(input, "distance");
            var angle = ReadOptionalDouble(input, "angle");
            var flipNormal = ReadBool(input, "flip_normal", false);
            var expectedAxes = ParseExpectedAxes(input["expected_axes"]);
            var expectedNormal = ParseExpectedNormal(input["expected_normal"]);
            var name = input["name"]?.GetValue<string>();
            return new RefPlaneArgs(subtype, refs, constraints, flips, modifiers, distance, angle,
                flipNormal, expectedAxes, expectedNormal, name);
        }

        internal bool PerReferenceFlip(int index) => index < Flips.Count && Flips[index];

        internal IReadOnlyList<RefPlaneModifier> PerReferenceModifiers(int index) =>
            index < Modifiers.Count ? Modifiers[index] : Empty;
    }

    private static RefPlaneSubtypeKind ParseSubtype(JsonNode? node) {
        // Default to ConstraintBase — the only authoring path.
        if (node is null) return RefPlaneSubtypeKind.ConstraintBase;
        var s = node.GetValue<string>();
        if (!Enum.TryParse<RefPlaneSubtypeKind>(s, ignoreCase: false, out var subtype)) {
            throw new ArgumentException(
                $"RefPlane: unknown subtype '{s}' (expected one of ConstraintBase, LinePoint, " +
                "ThreePoint, LineLine, Distance, Parallel, Angle, Normal, OnSurface, SWStandard)");
        }
        return subtype;
    }

    private static double[]? ParseExpectedNormal(JsonNode? node) {
        if (node is null) return null;
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefPlane: 'expected_normal' must be a JSON array of 3 numbers");
        }
        if (arr.Count != 3) {
            throw new ArgumentException(
                $"RefPlane: expected_normal must have exactly 3 entries; got {arr.Count}");
        }
        return [
            arr[0]!.GetValue<double>(),
            arr[1]!.GetValue<double>(),
            arr[2]!.GetValue<double>(),
        ];
    }

    // Optional informational replay input: the captured 4×3 frame (X, Y, Z=normal,
    // origin). When present, the Add brute-forces flips to reproduce it (with-sign).
    private static double[][]? ParseExpectedAxes(JsonNode? node) {
        if (node is null) return null;
        if (node is not JsonArray rows) {
            throw new ArgumentException("RefPlane: 'expected_axes' must be a JSON array of 4 rows");
        }
        if (rows.Count != 4) {
            throw new ArgumentException(
                $"RefPlane: expected_axes must have exactly 4 rows; got {rows.Count}");
        }
        var matrix = new double[4][];
        for (var i = 0; i < 4; i++) {
            if (rows[i] is not JsonArray row || row.Count != 3) {
                throw new ArgumentException($"RefPlane: expected_axes[{i}] must be a 3-entry array");
            }
            matrix[i] = [
                row[0]!.GetValue<double>(),
                row[1]!.GetValue<double>(),
                row[2]!.GetValue<double>(),
            ];
        }
        return matrix;
    }

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }

    private static List<Definition> ParseReferences(JsonNode? node) {
        // Non-ConstraintBase subtypes are Inspect-only; null/missing references = empty.
        if (node is null) return new List<Definition>();
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefPlane: 'references' must be a JSON array");
        }
        var list = new List<Definition>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var def = Definition.FromJson(arr[i])
                ?? throw new ArgumentException($"RefPlane: references[{i}] is not a valid Definition");
            list.Add(def);
        }
        return list;
    }

    private static List<bool> ParseFlips(JsonNode? node, int referencesCount) {
        if (node is null) return new List<bool>();
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefPlane: 'flips' must be a JSON array of booleans");
        }
        if (arr.Count != referencesCount) {
            throw new ArgumentException(
                $"RefPlane: flips length ({arr.Count}) must match references length ({referencesCount})");
        }
        var list = new List<bool>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var v = arr[i] ?? throw new ArgumentException($"RefPlane: flips[{i}] is null");
            list.Add(v.GetValue<bool>());
        }
        return list;
    }

    private static List<List<RefPlaneModifier>> ParseModifiers(JsonNode? node, int referencesCount) {
        if (node is null) return new List<List<RefPlaneModifier>>();
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefPlane: 'modifiers' must be a JSON array of string-arrays");
        }
        if (arr.Count != referencesCount) {
            throw new ArgumentException(
                $"RefPlane: modifiers length ({arr.Count}) must match references length ({referencesCount})");
        }
        var list = new List<List<RefPlaneModifier>>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var inner = arr[i];
            if (inner is null) {
                list.Add(new List<RefPlaneModifier>());
                continue;
            }
            if (inner is not JsonArray innerArr) {
                throw new ArgumentException(
                    $"RefPlane: modifiers[{i}] must be an array of modifier strings");
            }
            var slot = new List<RefPlaneModifier>(innerArr.Count);
            for (var j = 0; j < innerArr.Count; j++) {
                var s = innerArr[j]?.GetValue<string>()
                    ?? throw new ArgumentException($"RefPlane: modifiers[{i}][{j}] is null");
                if (!Enum.TryParse<RefPlaneModifier>(s, ignoreCase: false, out var m)) {
                    throw new ArgumentException(
                        $"RefPlane: unknown modifier '{s}' (expected 'OriginOnCurve' or 'ProjectToNearestLocation')");
                }
                slot.Add(m);
            }
            list.Add(slot);
        }
        return list;
    }

    private static List<RefPlaneConstraintKind> ParseConstraints(JsonNode? node) {
        if (node is null) return new List<RefPlaneConstraintKind>();
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefPlane: 'constraints' must be a JSON array");
        }
        var list = new List<RefPlaneConstraintKind>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var s = arr[i]?.GetValue<string>()
                ?? throw new ArgumentException($"RefPlane: constraints[{i}] is null");
            if (!Enum.TryParse<RefPlaneConstraintKind>(s, ignoreCase: false, out var kind)) {
                throw new ArgumentException(
                    $"RefPlane: unknown constraint '{s}' (expected one of Coincident, Parallel, Perpendicular, Distance, Angle, Tangent, ProjectOnto, MidPlane)");
            }
            list.Add(kind);
        }
        return list;
    }

    private static double? ReadOptionalDouble(JsonNode node, string field) {
        var v = node[field];
        if (v is null) return null;
        // Let GetValue<double>() throw on wrong-type so wire-shape errors surface.
        return v.GetValue<double>();
    }
}
