using System.Runtime.InteropServices;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Shared;

// SW quirk: `D1ReverseTwistDir` getter on `SweepFeatureData` returns False
// regardless of the authored "Reverse direction" UI state. The twist sign can't
// be read from the property directly; recover it by rolling back, force-clearing
// the flip, comparing post-rebuild body Definitions, and restoring on mismatch.
//
// Boss Sweep (`SweepHandler`) and Cut-Sweep (`CutSweepHandler`) both use the
// same `SweepFeatureData` interface so the probe applies uniformly. The body
// comparison is structural (BodyDefinition records) — a real D1Reverse-flip
// body shift is mm-scale on a typical sweep, while Parasolid's recompute jitter
// is sub-nanometer, so bit-exact record equality discriminates reliably.
internal static class TwistDirSignProbe {

    // Returns +1 if D1Reverse was effectively false, -1 if effectively true.
    // Caller multiplies abs(GetTwistAngle()) by this to get the signed angle
    // for the wire payload.
    //
    // Restoration uses `IModelDoc2.EditUndo2(1)` rather than a second
    // ModifyDefinition — undo atomically rewinds the bit AND any ambient
    // state SW recomputed during the rebuild. For Cut-Sweep that includes
    // the BodiesToKeep selection, which a manual-restore approach would
    // either lose or have to re-pin via PromptBodiesToKeepNotify.
    internal static int ProbeD1(File file, Feature feature, SweepFeatureData data) {
        try {
            // Park the rollback bar at AfterFeature so the rebuild is scoped
            // to THIS feature — downstream features stay rolled back and the
            // body diff faithfully reflects the sweep itself, not some
            // downstream cascade that might mask a real change.
            // (EditRollback is rollback-bar navigation, NOT an undoable
            // history entry, so it doesn't contend with EditUndo2's step
            // budget below.)
            file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);

            var before = SnapshotBodyDefs(file);

            // SW quirk: `EditUndo2(1)` on this stack empirically always
            // resets `D1ReverseTwistDir` to FALSE — it doesn't restore the
            // pre-modify state. So we exploit that determinism by probing
            // with `true` and only calling Undo when we KNOW the modify
            // changed something:
            //
            //   - Original D1=false: force=true changes the body (twist
            //     flips visibly). Undo (-> false) cleanly restores. Return +1.
            //
            //   - Original D1=true: force=true is a no-op (body unchanged).
            //     The source is already in its pre-probe state, so we skip
            //     the Undo (which would corrupt it back to false). Return -1.
            //
            // This avoids the "originally-true ends up false after probe"
            // failure mode that biting `false`-then-Undo had — and it removes
            // the need for a manual restore ModifyDefinition at the end.
            data.D1ReverseTwistDir = true;
            var modOk = feature.ModifyDefinition(data, file.ModelDoc, null);
            if (!modOk) {
                // SW rejected force=true — original was effectively false
                // (true isn't producible). No state to undo.
                return +1;
            }

            var after = SnapshotBodyDefs(file);
            var unchanged = BodyDefSetsEqual(before, after);

            if (!unchanged) {
                // Force=true changed the body → original was false. Undo
                // resets D1Reverse=false, which IS the original state.
                file.ModelDoc.EditUndo2(1);
                return +1;
            }

            // Force=true was a no-op → original was true. Source is already
            // in its pre-probe state; calling Undo here would change D1 to
            // false and corrupt the file.
            return -1;
        } catch (COMException) {
            throw new InvalidOperationException(
                $"Twist sign probe ({feature.Name}): COM exception during rollback / ModifyDefinition / undo");
        }
    }

    private static List<BodyDefinition> SnapshotBodyDefs(File file) {
        var defs = new List<BodyDefinition>();
        var raw = ((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
        if (raw is null) return defs;
        foreach (Body2 body in raw) {
            defs.Add(DefinitionCapture.Capture(body));
        }
        return defs;
    }

    // Order-insensitive set comparison of two BodyDefinition lists. Uses
    // record-level structural equality (bit-exact on every field including
    // centroid / volume / bbox doubles) — the probe is followed by an
    // EditUndo2 that rolls back to the pre-modify state, so we want to detect
    // ANY change ModifyDefinition produced, not just changes that exceed a
    // tolerance window. With a no-op rebuild Parasolid yields bit-identical
    // mass props; with a real D1Reverse-flip the body shifts mm-scale, so
    // bit-exact comparison reliably discriminates on nearly twist-symmetric
    // sweep shapes too.
    private static bool BodyDefSetsEqual(List<BodyDefinition> a, List<BodyDefinition> b) {
        if (a.Count != b.Count) return false;
        var matched = new bool[b.Count];
        foreach (var ad in a) {
            var found = false;
            for (var i = 0; i < b.Count; i++) {
                if (matched[i]) continue;
                if (ad == b[i]) {
                    matched[i] = true;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }
}
