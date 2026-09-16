using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Shared;

internal static class PatternInstances {
    // Per the SW 2026 docs for ICircularPatternFeatureData / ILinearPatternFeatureData:
    // axis=1, features=4, bodies=256, structure systems=134217728. Faces don't appear —
    // face patterns are a different feature kind (swFmFacePattern) with their own data
    // interface. PatternFaceArray on LinPat/CirPat is always empty.
    internal const int MarkFeatureSeed = 4;
    internal const int MarkBodySeed = 256;

    internal static void SelectSeeds(File file, IReadOnlyList<Definition> seeds) {
        if (seeds.Count == 0) {
            throw new ArgumentException("Pattern: 'seeds' must contain at least one entity");
        }
        foreach (var seedDef in seeds) {
            var live = DefinitionResolver.Resolve(file, seedDef)
                ?? throw new InvalidOperationException(
                    $"Pattern seed did not resolve: {seedDef.GetType().Name}");

            switch (live) {
                case Feature feat:
                    feat.Select2(true, MarkFeatureSeed);
                    break;
                case Body2 body:
                    // SW quirk: IBody2.Select2 takes (bool, SelectData); use Select(append, mark) for the bare-mark form.
                    body.Select(true, MarkBodySeed);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Pattern seed resolved to unsupported type {live.GetType().Name}");
            }
        }
    }

    internal static void SelectDirection(File file, Definition def, int mark, string label) {
        var live = DefinitionResolver.Resolve(file, def)
            ?? throw new InvalidOperationException($"Pattern {label} did not resolve");

        switch (live) {
            // SW quirk: Select4(append, SelectData) lives on IEntity — cast Edge/Face2 through.
            case Edge edge:
                ((IEntity)edge).Select4(true, MakeMarkSelectData(file, mark));
                break;
            case Face2 face:
                ((IEntity)face).Select4(true, MakeMarkSelectData(file, mark));
                break;
            case Feature feat:
                feat.Select2(true, mark);
                break;
            case SketchSegment seg:
                // SW quirk: ISketchSegment exposes Select4 (not Select5).
                seg.Select4(true, MakeMarkSelectData(file, mark));
                break;
            default:
                throw new InvalidOperationException(
                    $"Pattern {label} resolved to unsupported type {live.GetType().Name}");
        }
    }

    internal static SelectData MakeMarkSelectData(File file, int mark) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        return sd;
    }

    internal static void ApplySkippedInstances(Feature feature, IReadOnlyList<int> deleted, string label, File file) {
        if (deleted.Count == 0) return;

        // SW quirk: no SkipInstance(i) on any pattern interface — assign FeatureData.SkippedItemArray and commit via ModifyDefinition.
        var def = feature.GetDefinition();
        var skipped = deleted.ToArray();
        bool committed;
        try {
            switch (def) {
                case ILinearPatternFeatureData lin:
                    lin.AccessSelections(file.ModelDoc, null);
                    try {
                        lin.SkippedItemArray = skipped;
                        committed = feature.ModifyDefinition(lin, file.ModelDoc, null);
                    } finally {
                        lin.ReleaseSelectionAccess();
                    }
                    break;
                case ICircularPatternFeatureData circ:
                    circ.AccessSelections(file.ModelDoc, null);
                    try {
                        circ.SkippedItemArray = skipped;
                        committed = feature.ModifyDefinition(circ, file.ModelDoc, null);
                    } finally {
                        circ.ReleaseSelectionAccess();
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{label}: feature.GetDefinition() returned {def?.GetType().Name ?? "null"}, " +
                        "expected ILinearPatternFeatureData or ICircularPatternFeatureData");
            }
        } catch (Exception ex) when (ex is not InvalidOperationException) {
            throw new InvalidOperationException(
                $"{label}: applying 'deleted' [{string.Join(",", skipped)}] failed: {ex.Message}", ex);
        }
        if (!committed) {
            throw new InvalidOperationException(
                $"{label}: ModifyDefinition rejected 'deleted' [{string.Join(",", skipped)}] " +
                "(check indices are within the instance grid)");
        }
    }

    internal static IReadOnlyList<int> ReadSkippedItems(object? skippedRaw) {
        // SW quirk: SkippedItemArray returns null (not empty) when nothing is skipped.
        if (skippedRaw is not int[] arr) return Array.Empty<int>();
        return arr;
    }

    internal static JsonArray CaptureSeeds(object? featuresRaw, object? bodiesRaw, object? facesRaw) {
        var arr = new JsonArray();
        if (featuresRaw is object?[] features) {
            foreach (var f in features) {
                if (f is Feature feat) arr.Add(DefinitionCapture.Capture(feat).ToJson());
            }
        }
        if (bodiesRaw is object?[] bodies) {
            foreach (var b in bodies) {
                if (b is Body2 body) arr.Add(DefinitionCapture.Capture(body).ToJson());
            }
        }
        // Face seeds aren't a thing for swFmCirPattern/swFmLPattern (they belong to the
        // separate swFmFacePattern feature). If we ever see one in PatternFaceArray
        // here, we want to know — fail loud rather than silently drop it.
        if (facesRaw is object?[] faces && faces.Length > 0) {
            throw new InvalidOperationException(
                $"Pattern: PatternFaceArray contained {faces.Length} face(s); face seeds are not supported on swFmCirPattern/swFmLPattern (use swFmFacePattern).");
        }
        return arr;
    }

    internal static JsonNode CaptureDirectionRef(object? entity) {
        if (entity is null) {
            throw new InvalidOperationException("Pattern: direction reference is null");
        }
        // SW returns RefAxis for cylinder/cone temp axes; Capture(RefAxis) routes IsTempAxis through the underlying face.
        return DefinitionCapture.Capture(entity)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Pattern: direction reference is unsupported runtime type {entity.GetType().Name}");
    }

    internal static JsonArray ToIntArray(IReadOnlyList<int> values) {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    internal static List<Definition> ParseSeedList(JsonNode? node) {
        if (node is not JsonArray arr || arr.Count == 0) {
            throw new ArgumentException("Pattern: 'seeds' must be a non-empty array");
        }
        var seeds = new List<Definition>(arr.Count);
        foreach (var item in arr) {
            var def = Definition.FromJson(item)
                ?? throw new ArgumentException("Pattern: seed entry could not be parsed as a Definition");
            seeds.Add(def);
        }
        return seeds;
    }

    internal static double ReadDouble(JsonNode node, string field) {
        var v = node[field] ?? throw new ArgumentException($"Pattern: missing required field '{field}'");
        return v.GetValue<double>();
    }

    internal static int ReadInt(JsonNode node, string field) {
        var v = node[field] ?? throw new ArgumentException($"Pattern: missing required field '{field}'");
        return v.GetValue<int>();
    }

    internal static List<int> ReadIntList(JsonNode node, string field) {
        if (node[field] is not JsonArray arr) return new List<int>();
        var list = new List<int>(arr.Count);
        foreach (var item in arr) {
            if (item is not null) list.Add(item.GetValue<int>());
        }
        return list;
    }

    internal static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }
}
