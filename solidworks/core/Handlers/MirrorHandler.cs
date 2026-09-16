using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class MirrorHandler {
    public const string TypeName = "Mirror";
    public const string PatternType = "MirrorPattern";
    public const string BodyType = "MirrorSolid";
    private const int MarkPlane = 2;
    private const int MarkSecondaryPlane = 4096;
    private const int MarkFeatureScope = 512;

    public static JsonNode Add(File file, JsonNode input) {
        var args = Args.Parse(input);
        var seeds = ResolveSeeds(file, args.Seeds);
        var plane = ResolvePlane(file, args.Plane);
        if (args.SecondaryPlane is not null) _ = ResolvePlane(file, args.SecondaryPlane);
        var bodies = seeds.All(s => s is Body2);
        ValidateMode(args, bodies);
        file.ModelDoc.ClearSelection2(true);
        foreach (var seed in seeds.OrderByDescending(SeedMark)) Select(file, seed, SeedMark(seed));
        foreach (var body in ResolveSeeds(file, args.FeatureScope)) Select(file, body, MarkFeatureScope);
        Select(file, plane, MarkPlane);
        var feature = file.ModelDoc.FeatureManager.InsertMirrorFeature2(
            bodies, args.GeometryPattern, args.Merge, args.KnitSurfaces, args.Scope)
            ?? throw new InvalidOperationException("Mirror: InsertMirrorFeature2 failed; check the seeds, plane and merge options");
        File.ApplyFeatureName(feature, input);
        ApplyDefinition(file, feature, args);
        SetSecondaryPlane(file, feature, args);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return Inspect(file, feature);
    }

    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = Args.Parse(input);
        ApplyDefinition(file, feature, args);
        SetSecondaryPlane(file, feature, args);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        return Inspect(file, feature);
    }

    private static void ApplyDefinition(File file, Feature feature, Args args) {
        var data = feature.GetDefinition();
        switch (data) {
            case IMirrorSolidFeatureData body:
                if (!body.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Mirror: cannot access body selections");
                try {
                    var seeds = ResolveSeeds(file, args.Seeds);
                    ValidateMode(args, true);
                    if (seeds.Any(s => s is not Body2)) throw new ArgumentException("Mirror: cannot change a body mirror into a feature or face mirror");
                    body.Face = ResolvePlane(file, args.Plane);
                    body.PatternBodyArray = Wrap(seeds);
                    SetBodyOptions(body, args);
                    CommitDefinition(file, feature, body);
                } finally { body.ReleaseSelectionAccess(); }
                break;
            case IMirrorPatternFeatureData pattern:
                if (!pattern.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Mirror: cannot access feature selections");
                try {
                    var seeds = ResolveSeeds(file, args.Seeds);
                    ValidateMode(args, false);
                    if (seeds.Any(s => s is Body2)) throw new ArgumentException("Mirror: cannot change a feature or face mirror into a body mirror");
                    var plane = ResolvePlane(file, args.Plane);
                    // SW quirk: passing the Feature wrapper here silently clears the plane.
                    pattern.Plane = plane is Feature planeFeature ? planeFeature.GetSpecificFeature2() : plane;
                    pattern.PatternFeatureArray = Wrap(seeds.OfType<Feature>().Cast<object>());
                    pattern.MirrorFaceArray = Wrap(seeds.OfType<Face2>().Cast<object>());
                    pattern.GeometryPattern = args.GeometryPattern;
                    pattern.PropagateVisualProperty = args.PropagateVisualProperties;
                    pattern.FeatureScope = args.Scope;
                    pattern.FeatureScopeBodies = FeatureScope.ResolveScopeBodies(file, args.FeatureScope);
                    CommitDefinition(file, feature, pattern);
                } finally { pattern.ReleaseSelectionAccess(); }
                break;
            default: throw new NotSupportedException($"Mirror: unsupported definition for {feature.Name}");
        }
    }

    private static void CommitDefinition(File file, Feature feature, object data) {
        if (!feature.ModifyDefinition(data, file.ModelDoc, null))
            throw new InvalidOperationException($"Mirror: ModifyDefinition failed for {feature.Name}");
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var result = new JsonObject { ["type"] = TypeName, ["name"] = feature.Name };
        switch (feature.GetDefinition()) {
            case IMirrorSolidFeatureData body:
                if (!body.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Mirror: cannot inspect body selections");
                try {
                    if (body.StructureSystemToPatternArray is object[] structures && structures.Length > 0)
                        throw new NotSupportedException("Mirror: structure-system seeds are not supported");
                    result["plane"] = Capture(body.Face);
                    result["seeds"] = CaptureArray(body.PatternBodyArray);
                    result["merge"] = body.Merge;
                    result["knit_surfaces"] = body.KnitSurface;
                    result["propagate_visual_properties"] = body.PropagateVisualProperty;
                } finally { body.ReleaseSelectionAccess(); }
                break;
            case IMirrorPatternFeatureData pattern:
                if (!pattern.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Mirror: cannot inspect feature selections");
                try {
                    result["plane"] = Capture(pattern.Plane);
                    var seeds = CaptureArray(pattern.PatternFeatureArray);
                    foreach (var face in CaptureArray(pattern.MirrorFaceArray)) seeds.Add(face?.DeepClone());
                    result["seeds"] = seeds;
                    result["geometry_pattern"] = pattern.GeometryPattern;
                    result["propagate_visual_properties"] = pattern.PropagateVisualProperty;
                    result["scope"] = ScopeName(pattern.FeatureScope);
                    result["feature_scope"] = pattern.FeatureScope == 0 ? new JsonArray() : CaptureArray(pattern.FeatureScopeBodies);
                } finally { pattern.ReleaseSelectionAccess(); }
                break;
            default: throw new NotSupportedException($"Mirror: unsupported definition for {feature.Name}");
        }
        // SW quirk: neither the data interfaces nor GetParents expose the secondary plane.
        using var page = new MirrorPage(file, feature);
        var secondary = file.ModelDoc.ISelectionManager.GetSelectedObject6(1, MarkSecondaryPlane);
        result["secondary_plane"] = secondary is null ? null : Capture(secondary);
        result["mirror_seed_only"] = secondary is not null && page.SeedOnly;
        return result;
    }

    private static void SetSecondaryPlane(File file, Feature feature, Args args) {
        // SW quirk: InsertMirrorFeature2 ignores mark 4096; bind it through Edit Feature.
        using var page = new MirrorPage(file, feature);
        var selections = file.ModelDoc.ISelectionManager;
        for (var i = selections.GetSelectedObjectCount2(-1); i > 0; i--)
            if (selections.GetSelectedObjectMark(i) == MarkSecondaryPlane) selections.DeSelect2(i, -1);
        if (args.SecondaryPlane is not null) {
            Select(file, ResolvePlane(file, args.SecondaryPlane), MarkSecondaryPlane);
            page.SeedOnly = args.MirrorSeedOnly;
        }
        page.Commit();
        // SW quirk: committing a secondary-plane edit clears the body mirror's merge flag.
        if (feature.GetDefinition() is IMirrorSolidFeatureData body) {
            if (!body.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Mirror: cannot restore body options");
            try {
                SetBodyOptions(body, args);
                CommitDefinition(file, feature, body);
            } finally { body.ReleaseSelectionAccess(); }
        }
    }

    private static void SetBodyOptions(IMirrorSolidFeatureData body, Args args) {
        body.Merge = args.Merge;
        body.KnitSurface = args.KnitSurfaces;
        body.PropagateVisualProperty = args.PropagateVisualProperties;
    }

    private static object ResolvePlane(File file, Definition definition) {
        var live = DefinitionResolver.Resolve(file, definition);
        if (live is Feature feature && feature.GetTypeName2() == "RefPlane") return feature;
        if (live is Face2 face && face.IGetSurface().IsPlane()) return face;
        throw new ArgumentException("Mirror: a mirror plane must resolve to a reference plane or planar face");
    }

    private static object[] ResolveSeeds(File file, IReadOnlyList<Definition> definitions) {
        var seeds = definitions.Select(d => DefinitionResolver.Resolve(file, d)
            ?? throw new ArgumentException("Mirror: a seed did not resolve")).ToArray();
        foreach (var seed in seeds) _ = SeedMark(seed);
        if (seeds.Any(s => s is Body2) && seeds.Any(s => s is not Body2))
            throw new ArgumentException("Mirror: body seeds cannot be mixed with features or faces");
        return seeds;
    }

    private static int SeedMark(object seed) => seed switch {
        Feature => 1, Face2 => 128, Body2 => 256,
        _ => throw new ArgumentException("Mirror: seeds must be features, faces or bodies"),
    };

    private static void Select(File file, object live, int mark) {
        if (!Definition.SelectLive(file, live, mark)) throw new InvalidOperationException($"Mirror: selection failed at mark {mark}");
    }

    private static JsonNode Capture(object live) => DefinitionCapture.Capture(live)?.ToJson()
        ?? throw new NotSupportedException($"Mirror: cannot capture {live?.GetType().Name ?? "null"}");

    private static JsonArray CaptureArray(object? value) => new(((object[]?)value ?? []).Select(Capture).ToArray());
    private static DispatchWrapper[] Wrap(IEnumerable<object> objects) => objects.Select(o => new DispatchWrapper(o)).ToArray();
    private static string ScopeName(int scope) => scope switch {
        0 => "AllBodies", 1 => "AutoSelect", 2 => "SelectedBodies",
        _ => throw new NotSupportedException($"Mirror: unknown feature scope {scope}"),
    };

    private static void ValidateMode(Args args, bool bodies) {
        if (bodies && (args.GeometryPattern || args.Scope != 0 || args.FeatureScope.Count > 0))
            throw new ArgumentException("Mirror: geometry_pattern and feature_scope apply to feature or face seeds");
        if (!bodies && (!args.Merge || args.KnitSurfaces))
            throw new ArgumentException("Mirror: merge and knit_surfaces apply to body seeds");
    }

    private sealed record Args(Definition Plane, IReadOnlyList<Definition> Seeds, Definition? SecondaryPlane,
        bool MirrorSeedOnly, bool GeometryPattern, bool Merge, bool KnitSurfaces,
        bool PropagateVisualProperties, int Scope, IReadOnlyList<Definition> FeatureScope) {
        public static Args Parse(JsonNode input) {
            var secondary = input["secondary_plane"] is { } node
                ? Definition.FromJson(node) ?? throw new ArgumentException("Mirror: invalid secondary_plane") : null;
            var seedOnly = input["mirror_seed_only"]?.GetValue<bool>() ?? false;
            if (seedOnly && secondary is null) throw new ArgumentException("Mirror: mirror_seed_only requires secondary_plane");
            var scope = (input["scope"]?.GetValue<string>() ?? "AllBodies") switch {
                "AllBodies" => 0, "AutoSelect" => 1, "SelectedBodies" => 2,
                var s => throw new ArgumentException($"Mirror: unknown scope {s}"),
            };
            var scopeBodies = Definition.FromJsonArray(input["feature_scope"] ?? new JsonArray(), "Mirror.feature_scope");
            if (scopeBodies.Any(d => d is not BodyDefinition)) throw new ArgumentException("Mirror: feature_scope requires body definitions");
            if ((scope == 2 && scopeBodies.Count == 0) || (scope == 0 && scopeBodies.Count > 0))
                throw new ArgumentException("Mirror: feature_scope must agree with scope");
            return new Args(Definition.FromJson(input["plane"]) ?? throw new ArgumentException("Mirror: missing plane"),
                PatternInstances.ParseSeedList(input["seeds"]), secondary, seedOnly,
                input["geometry_pattern"]?.GetValue<bool>() ?? false, input["merge"]?.GetValue<bool>() ?? true,
                input["knit_surfaces"]?.GetValue<bool>() ?? false,
                input["propagate_visual_properties"]?.GetValue<bool>() ?? true, scope, scopeBodies);
        }
    }

    private sealed class MirrorPage : IDisposable {
        private readonly File _file;
        private readonly bool _wasLocked;
        private bool _closed;
        private static bool IsMirrorPage(string name) => name is "MirrPatternDveDlg" or "uiMirrPatternDveDlg_c";
        private const int SeedOnlyControlId = 48300;
        private const uint BmGetCheck = 0x00F0;
        private const uint BmClick = 0x00F5;

        public MirrorPage(File file, Feature feature) {
            _file = file;
            if (!string.IsNullOrEmpty(file.ModelDoc.Extension.GetActivePropertyManagerPage()))
                throw new InvalidOperationException("Mirror: finish the active PropertyManager before editing a mirror");
            _wasLocked = file.IsLocked;
            if (_wasLocked) file.Unlock();
            try {
                file.ModelDoc.ClearSelection2(true);
                feature.Select2(false, -1);
                file.ModelDoc.FeatEditDef();
                RequirePage();
            } catch { Dispose(); throw; }
        }

        public bool SeedOnly {
            get => Send(SeedOnlyControl(), BmGetCheck) != IntPtr.Zero;
            set {
                var control = SeedOnlyControl();
                if ((Send(control, BmGetCheck) != IntPtr.Zero) == value) return;
                if (!NativeControls.IsWindowEnabled(control)) throw new ArgumentException("Mirror: mirror_seed_only requires perpendicular mirror planes");
                Send(control, BmClick);
                if ((Send(control, BmGetCheck) != IntPtr.Zero) != value)
                    throw new InvalidOperationException("Mirror: the seed-only option did not update");
            }
        }

        public void Commit() {
            RequirePage();
            _file.ModelDoc.Extension.RunCommand((int)swCommands_e.swCommands_Ok_Command, "");
            if (!string.IsNullOrEmpty(_file.ModelDoc.Extension.GetActivePropertyManagerPage()))
                throw new InvalidOperationException("Mirror: SolidWorks rejected the mirror options");
            _closed = true;
        }

        private void RequirePage() {
            if (!IsMirrorPage(_file.ModelDoc.Extension.GetActivePropertyManagerPage()))
                throw new InvalidOperationException($"Mirror: the mirror PropertyManager did not open (active: {_file.ModelDoc.Extension.GetActivePropertyManagerPage()})");
        }

        private IntPtr SeedOnlyControl() {
            // SW exposes this option only in its native page, not either mirror data interface.
            RequirePage();
            return NativeControls.Find((IntPtr)_file.SldWorks.IFrameObject().GetHWndx64(), SeedOnlyControlId);
        }

        public void Dispose() {
            try {
                if (!_closed && IsMirrorPage(_file.ModelDoc.Extension.GetActivePropertyManagerPage())) {
                    _file.ModelDoc.Extension.RunCommand((int)swCommands_e.swCommands_Cancel_Command, "");
                }
            } finally {
                if (_wasLocked) _file.Lock();
            }
        }

        private static IntPtr Send(IntPtr window, uint message) => NativeControls.Send(window, message);
    }
}

