using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core;

public partial class File {
    internal AssemblyDoc AssemblyDoc => Kind == "assembly" ? (AssemblyDoc)_modelDoc
        : throw new InvalidOperationException($"'{Name}' is not an assembly");

    private Component2 RootComponent => _modelDoc.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true)
        ?? throw new InvalidOperationException("Assembly has no active root component");

    internal static string[] ComponentPath(Component2 component) => component.Name2.Split('/');

    private static IEnumerable<Component2> Children(Component2 component) =>
        ((object[]?)component.GetChildren() ?? []).Cast<Component2>();

    internal Component2 ResolveComponent(IReadOnlyList<string> path) {
        _ = AssemblyDoc;
        if (path.Count == 0 || path.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Component path must contain nonempty instance names");
        var current = RootComponent;
        foreach (var name in path) {
            if (current.IsSuppressed())
                throw new InvalidOperationException($"Component '{current.Name2}' is suppressed; unsuppress it before accessing '{string.Join('/', path)}'");
            current = Children(current).SingleOrDefault(child => ComponentPath(child)[^1] == name)
                ?? throw new ArgumentException($"Component not found: {string.Join('/', path)}");
        }
        return current;
    }

    private void Activate(ModelDoc2 doc) {
        var errors = 0;
        var active = _sldWorks.ActivateDoc3(doc.GetTitle(), false,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
        if (active is null || errors != 0 || _sldWorks.IsSame(active, doc) != (int)swObjectEquality.swObjectSame)
            throw new InvalidOperationException($"Could not activate '{doc.GetTitle()}': {errors}");
    }

    internal T WithComponentSource<T>(Component2 component, Func<File, T> operation, bool activate = false) {
        if (component.IsSuppressed())
            throw new InvalidOperationException($"Component '{component.Name2}' is suppressed; unsuppress it before accessing its geometry");
        if (component.GetModelDoc2() is not ModelDoc2) SetComponentSuppression(component, false);
        var doc = component.GetModelDoc2() as ModelDoc2
            ?? throw new InvalidOperationException($"Component document is unavailable: {component.Name2}");
        var previousConfiguration = doc.ConfigurationManager.ActiveConfiguration.Name;
        var configuration = component.ReferencedConfiguration;
        var previousActive = _sldWorks.ActiveDoc as ModelDoc2;
        try {
            if (previousConfiguration != configuration && !doc.ShowConfiguration2(configuration))
                throw new InvalidOperationException($"Cannot activate configuration '{configuration}' for {component.Name2}");
            if (activate) Activate(doc);
            using var scope = MassProperties.Push(doc);
            return operation(new File(doc, _sldWorks));
        } finally {
            if (previousConfiguration != configuration && !doc.ShowConfiguration2(previousConfiguration))
                throw new InvalidOperationException($"Cannot restore configuration '{previousConfiguration}'");
            if (activate && previousActive is not null) Activate(previousActive);
        }
    }

    internal object ResolveComponentEntity(ComponentEntityDefinition definition) {
        var component = ResolveComponent(definition.Component);
        return WithComponentSource(component, source => {
            var entity = definition.Entity.Resolve(source)
                ?? throw new ArgumentException($"Entity did not resolve on {component.Name2}: {definition.Entity.ToJson()}");
            var corresponding = entity is Feature or RefPlane or RefAxis or SketchSegment or SketchPoint
                ? component.GetCorresponding(entity)
                : component.GetCorrespondingEntity(entity);
            return corresponding ?? throw new InvalidOperationException($"Cannot map entity into {component.Name2}");
        });
    }

    private static object UnderlyingEntity(File source, object entity) {
        var corresponding = entity is Feature or RefPlane or RefAxis or SketchSegment or SketchPoint
            ? source.ModelDoc.Extension.GetCorresponding2(entity)
            : source.ModelDoc.Extension.GetCorrespondingEntity2(entity);
        return corresponding ?? throw new InvalidOperationException("Cannot map assembly entity into its source document");
    }

    internal Definition CaptureAssemblyEntity(object entity, Component2? component) {
        if (entity is Component2 whole) return new ComponentDefinition(ComponentPath(whole));
        // Native assembly datums can report the root component instead of null.
        if (component is null || component.IsRoot()) return DefinitionCapture.Capture(entity)
            ?? throw new NotSupportedException("Unsupported assembly-owned reference");
        return WithComponentSource(component, source => {
            var local = UnderlyingEntity(source, entity);
            var definition = DefinitionCapture.Capture(local)
                ?? throw new NotSupportedException($"Unsupported entity on {component.Name2}");
            return new ComponentEntityDefinition(ComponentPath(component), definition);
        });
    }

    public JsonNode? ProbeComponent(JsonNode path, JsonNode ray, string entityType, JsonNode? onBody = null) {
        var component = ResolveComponent(ReadComponentPath(path));
        return WithComponentSource(component, source => {
            var local = source.Probe(ray, entityType, onBody);
            if (local is null) return null;
            var definition = Definition.FromJson(local)
                ?? throw new InvalidOperationException("Probe returned a malformed Definition");
            var result = new ComponentEntityDefinition(ComponentPath(component), definition).ToJson();
            if (local["echo_probe_ambiguous"] is { } ambiguous) result["echo_probe_ambiguous"] = ambiguous.DeepClone();
            return result;
        }, activate: true);
    }

    public JsonNode GenerateComponentProbe(JsonNode path, JsonNode definition, string entityType, int startAttempt = 0) =>
        WithComponentSource(ResolveComponent(ReadComponentPath(path)),
            source => source.GenerateProbe(definition, entityType, startAttempt), activate: true);

    public JsonNode AddComponent(JsonNode input) {
        var assembly = AssemblyDoc;
        var color = ReadComponentColor(input["color"]);
        var sourcePath = System.IO.Path.GetFullPath(input["source"]!.GetValue<string>());
        var source = new Session(_sldWorks).OpenFile(sourcePath, background: true);
        var configuration = input["configuration"]?.GetValue<string>()
            ?? source.ModelDoc.ConfigurationManager.ActiveConfiguration.Name;
        if (source.ModelDoc.GetConfigurationByName(configuration) is null)
            throw new ArgumentException($"Configuration '{configuration}' does not exist in '{sourcePath}'");
        var placement = RigidTransform.Parse(input["transform"]);
        Activate(_modelDoc);
        var component = assembly.AddComponent5(sourcePath,
            (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
            "", true, configuration, 0, 0, 0)
            ?? throw new InvalidOperationException($"Cannot insert component '{sourcePath}'");
        SetComponentFixed(component, false);
        component.Transform2 = placement.ToNative(_mathUtil);
        SetComponentFixed(component, JsonHelpers.ReadBool(input, "fixed", false));
        component.Visible = JsonHelpers.ReadBool(input, "visible", true)
            ? (int)swComponentVisibilityState_e.swComponentVisible : (int)swComponentVisibilityState_e.swComponentHidden;
        if (color is not null) SetComponentColor(component, color);
        RebuildModel();
        placement.RequireMatch(component.Transform2, component.Name2);
        if (JsonHelpers.ReadBool(input, "suppressed", false)) SetComponentSuppression(component, true);
        return InspectComponent(component);
    }

    public JsonNode InspectComponent(JsonNode path) => InspectComponent(ResolveComponent(ReadComponentPath(path)));

    private JsonNode InspectComponent(Component2 component) {
        var suppressed = component.IsSuppressed();
        var placement = component.Transform2;
        return new JsonObject {
            ["path"] = JsonSerializer.SerializeToNode(ComponentPath(component)),
            ["source"] = component.GetPathName(),
            ["configuration"] = component.ReferencedConfiguration,
            ["transform"] = placement is null ? null : RigidTransform.Capture(placement).ToJson(),
            ["fixed"] = component.IsFixed(),
            ["visible"] = component.Visible == (int)swComponentVisibilityState_e.swComponentVisible,
            ["suppressed"] = suppressed,
            ["color"] = ComponentColor(component),
            ["constraint_status"] = suppressed ? "suppressed" : ConstraintStatus(component.GetConstrainedStatus()),
            ["children"] = new JsonArray(Children(component).Select(InspectComponent).ToArray()),
        };
    }

    public JsonNode GetComponents() {
        _ = AssemblyDoc;
        return new JsonArray(Children(RootComponent).Select(InspectComponent).ToArray());
    }

    public JsonNode EditComponent(JsonNode path, JsonNode changes) {
        var component = ResolveComponent(ReadComponentPath(path));
        var color = ReadComponentColor(changes["color"]);
        if (changes["transform"] is not null)
            throw new ArgumentException("Use TranslateComponent or RotateComponent for movement; absolute transforms are only accepted at insertion");
        var allowed = new[] { "configuration", "fixed", "visible", "suppressed", "color" };
        foreach (var key in changes.AsObject().Select(pair => pair.Key))
            if (!allowed.Contains(key)) throw new ArgumentException($"Component field '{key}' is not editable");
        if (changes["suppressed"]?.GetValue<bool>() == false) SetComponentSuppression(component, false);
        if (component.IsSuppressed() && changes.AsObject().Any(pair => pair.Key != "suppressed"))
            throw new InvalidOperationException("Unsuppress the component before editing its properties");
        if (changes["fixed"]?.GetValue<bool>() == false) SetComponentFixed(component, false);
        if (changes["configuration"] is { } configurationNode) {
            var configuration = configurationNode.GetValue<string>();
            WithComponentSource(component, source => {
                if (source.ModelDoc.GetConfigurationByName(configuration) is null)
                    throw new ArgumentException($"Configuration '{configuration}' does not exist");
                return true;
            });
            component.ReferencedConfiguration = configuration;
        }
        if (changes["fixed"]?.GetValue<bool>() == true) SetComponentFixed(component, true);
        if (changes["visible"] is { } visible) component.Visible = visible.GetValue<bool>()
            ? (int)swComponentVisibilityState_e.swComponentVisible : (int)swComponentVisibilityState_e.swComponentHidden;
        if (changes.AsObject().ContainsKey("color")) SetComponentColor(component, color);
        RebuildModel();
        if (changes["suppressed"]?.GetValue<bool>() == true) SetComponentSuppression(component, true);
        return InspectComponent(component);
    }

    public JsonNode DeleteComponent(JsonNode path) {
        var component = ResolveComponent(ReadComponentPath(path));
        _modelDoc.ClearSelection2(true);
        if (!Definition.SelectLive(this, component, 0) || !_modelDoc.Extension.DeleteSelection2(0))
            throw new InvalidOperationException($"Cannot delete component {component.Name2}");
        RebuildModel();
        return new JsonObject { ["deleted"] = true, ["path"] = path.DeepClone() };
    }

    private void SetComponentFixed(Component2 component, bool fixedPosition) {
        if (component.IsFixed() == fixedPosition) return;
        _modelDoc.ClearSelection2(true);
        if (!Definition.SelectLive(this, component, 0)) throw new InvalidOperationException($"Cannot select {component.Name2}");
        if (fixedPosition) AssemblyDoc.FixComponent(); else AssemblyDoc.UnfixComponent();
        _modelDoc.ClearSelection2(true);
        if (component.IsFixed() != fixedPosition) throw new InvalidOperationException($"Could not change fixed state of {component.Name2}");
    }

    private static void SetComponentSuppression(Component2 component, bool suppressed) {
        var result = component.SetSuppression2(suppressed
            ? (int)swComponentSuppressionState_e.swComponentSuppressed
            : (int)swComponentSuppressionState_e.swComponentFullyResolved);
        if (result != (int)swSuppressionError_e.swSuppressionChangeOk)
            throw new InvalidOperationException($"Cannot change suppression of {component.Name2}: {(swSuppressionError_e)result}");
    }

    internal void RebuildModel() {
        if (!_modelDoc.EditRebuild3()) throw new InvalidOperationException($"Rebuild failed in '{Name}'; inspect the file for failed features");
    }

    public JsonNode Rebuild() {
        RebuildModel();
        return View();
    }

    internal static IReadOnlyList<string> ReadComponentPath(JsonNode path) =>
        path.Deserialize<string[]>() ?? throw new ArgumentException("Expected a component occurrence path");

    private JsonNode ViewAssembly() {
        var features = new JsonArray();
        var index = 0;
        foreach (var feature in Features()) {
            var currentIndex = index++;
            var nativeType = feature.GetTypeName2();
            if (nativeType is "CommentsFolder" or "FavoriteFolder" or "HistoryFolder" or "SelectionSetFolder"
                or "SensorFolder" or "LiveSectionFolder" or "DocsFolder" or "DetailCabinet" or "EnvFolder"
                or "InkMarkupFolder" or "EqnFolder" or "MateGroup" or "Reference") continue;
            features.Add(new JsonObject {
                ["index"] = currentIndex, ["name"] = feature.Name,
                ["type"] = ResolveFeatureTypeName(feature), ["status"] = ReadFeatureStatus(feature),
            });
        }
        var box = (double[]?)AssemblyDoc.GetBox(0);
        return new JsonObject {
            ["kind"] = Kind, ["path"] = Path, ["units"] = CurrentUnitName,
            ["configuration"] = _modelDoc.ConfigurationManager.ActiveConfiguration.Name,
            ["components"] = GetComponents(), ["features"] = features,
            ["bbox"] = box is null ? null : new JsonObject {
                ["min"] = new JsonArray(box[0], box[1], box[2]),
                ["max"] = new JsonArray(box[3], box[4], box[5]),
            },
        };
    }

    private static string ConstraintStatus(int status) => (swConstrainedStatus_e)status switch {
        swConstrainedStatus_e.swUnknownConstraint => "unknown",
        swConstrainedStatus_e.swUnderConstrained => "underconstrained",
        swConstrainedStatus_e.swFullyConstrained => "fully_constrained",
        swConstrainedStatus_e.swOverConstrained => "overconstrained",
        swConstrainedStatus_e.swNoSolution => "no_solution",
        swConstrainedStatus_e.swInvalidSolution => "invalid_solution",
        swConstrainedStatus_e.swAutosolveOff => "solver_disabled",
        _ => throw new NotSupportedException($"Unknown component constraint status: {status}"),
    };

    private JsonNode? ProbeAssembly(JsonNode rayNode, string entityType, JsonNode? onBody) {
        if (onBody is not null) throw new ArgumentException("Use component.probe(..., on_body=...) to scope assembly geometry");
        var ray = rayNode.Deserialize<Ray>(Definition.JsonOptions) ?? throw new ArgumentException("Malformed ray");
        var filter = entityType switch {
            "face" => swSelectType_e.swSelFACES, "edge" => swSelectType_e.swSelEDGES,
            "vertex" => swSelectType_e.swSelVERTICES, "plane" => swSelectType_e.swSelDATUMPLANES,
            _ => throw new NotSupportedException($"Assembly probe does not support '{entityType}'; use component.probe"),
        };
        _modelDoc.ClearSelection2(true);
        try {
            var p = new[] { ray.Origin.X, ray.Origin.Y, ray.Origin.Z };
            var d = new[] { ray.Direction.X, ray.Direction.Y, ray.Direction.Z };
            _modelDoc.IMultiSelectByRay(ref p[0], ref d[0], ray.Radius ?? DefaultProbeRadius, (int)filter, false);
            var manager = _modelDoc.ISelectionManager;
            var hits = new List<(Definition Definition, double Distance)>();
            for (var i = 1; i <= manager.GetSelectedObjectCount2(-1); i++) {
                var entity = manager.GetSelectedObject6(i, -1);
                var component = manager.GetSelectedObjectsComponent4(i, -1) as Component2;
                var localRay = component is null ? ray : TransformRay(ray, component.Transform2.IInverse());
                var localEntity = component is null ? entity : WithComponentSource(component, source => UnderlyingEntity(source, entity));
                using var scope = MassProperties.Push(_modelDoc);
                var distance = RayAxisDistance(localEntity, localRay, out _, out _);
                hits.Add((CaptureAssemblyEntity(entity, component), distance));
            }
            if (hits.Count == 0) return null;
            var ordered = hits.OrderBy(hit => hit.Distance).ToArray();
            if (ordered.Length > 1 && Math.Abs(ordered[0].Distance - ordered[1].Distance) <= Flags.GeometryTolerance)
                throw new InvalidOperationException("Assembly probe is ambiguous; use component.probe to select the intended occurrence");
            return ordered[0].Definition.ToJson();
        } finally { _modelDoc.ClearSelection2(true); }
    }

    private Ray TransformRay(Ray ray, MathTransform transform) {
        var point = (double[])((MathPoint)((MathPoint)_mathUtil.CreatePoint(new[] { ray.Origin.X, ray.Origin.Y, ray.Origin.Z }))
            .MultiplyTransform(transform)).ArrayData;
        var direction = (double[])((MathVector)((MathVector)_mathUtil.CreateVector(new[] { ray.Direction.X, ray.Direction.Y, ray.Direction.Z }))
            .MultiplyTransform(transform)).ArrayData;
        return new Ray(new Point3D(point[0], point[1], point[2]), new Direction(direction[0], direction[1], direction[2]), ray.Radius);
    }
}

internal sealed record RigidTransform(
    [property: JsonPropertyName("translation")] Point3D Translation,
    [property: JsonPropertyName("rotation")] double[][] Rotation) {

    internal static RigidTransform Parse(JsonNode? node) {
        var value = node is null ? new RigidTransform(new Point3D(0, 0, 0), [[1, 0, 0], [0, 1, 0], [0, 0, 1]])
            : node.Deserialize<RigidTransform>(Definition.JsonOptions) ?? throw new ArgumentException("Malformed transform");
        if (value.Rotation is null || value.Rotation.Length != 3 || value.Rotation.Any(row => row is null || row.Length != 3)
            || value.Translation is null)
            throw new ArgumentException("Transform requires translation and a 3x3 rotation matrix");
        var r = value.Rotation;
        if (!r.SelectMany(row => row).Concat([value.Translation.X, value.Translation.Y, value.Translation.Z]).All(double.IsFinite))
            throw new ArgumentException("Transform values must be finite");
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
            if (Math.Abs(Enumerable.Range(0, 3).Sum(k => r[i][k] * r[j][k]) - (i == j ? 1 : 0)) > 1e-9)
                throw new ArgumentException("Rotation must be orthonormal");
        var determinant = r[0][0]*(r[1][1]*r[2][2]-r[1][2]*r[2][1])
            - r[0][1]*(r[1][0]*r[2][2]-r[1][2]*r[2][0]) + r[0][2]*(r[1][0]*r[2][1]-r[1][1]*r[2][0]);
        if (Math.Abs(determinant - 1) > 1e-9) throw new ArgumentException("Rotation cannot scale or reflect geometry");
        return value;
    }

    internal MathTransform ToNative(MathUtility math) {
        var values = new double[16];
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) values[i*3+j] = Rotation[j][i];
        values[9] = Translation.X; values[10] = Translation.Y; values[11] = Translation.Z; values[12] = 1;
        return (MathTransform)math.CreateTransform(values);
    }

    internal static RigidTransform Capture(MathTransform transform) {
        var a = (double[])transform.ArrayData;
        if (Math.Abs(a[12] - 1) > 1e-9) throw new NotSupportedException("Scaled component transforms are not supported");
        return new RigidTransform(new Point3D(a[9], a[10], a[11]),
            [[a[0], a[3], a[6]], [a[1], a[4], a[7]], [a[2], a[5], a[8]]]);
    }

    internal JsonNode ToJson() => JsonSerializer.SerializeToNode(this)!;

    internal double[] RotationXyz() {
        var r = Rotation;
        // Native By Delta XYZ composes Rx * Ry * Rz in the assembly frame.
        var y = Math.Asin(Math.Clamp(r[0][2], -1, 1));
        if (Math.Abs(Math.Cos(y)) < 1e-8) return [Math.Atan2(Math.Sign(y)*r[1][0], r[1][1]), y, 0];
        return [Math.Atan2(-r[1][2], r[2][2]), y, Math.Atan2(-r[0][1], r[0][0])];
    }

    internal (double Translation, double Rotation) Difference(MathTransform actual) {
        var found = Capture(actual);
        var delta = new[] { Math.Abs(found.Translation.X - Translation.X), Math.Abs(found.Translation.Y - Translation.Y),
            Math.Abs(found.Translation.Z - Translation.Z) }.Max();
        var rotationDelta = Rotation.SelectMany(row => row).Zip(found.Rotation.SelectMany(row => row), (a, b) => Math.Abs(a-b)).Max();
        return (delta, rotationDelta);
    }

    internal void RequireMatch(MathTransform actual, string component) {
        var delta = Difference(actual);
        if (delta.Translation > Flags.GeometryTolerance || delta.Rotation > 1e-7)
            throw new InvalidOperationException($"Requested placement was not achieved for '{component}': translation error {delta.Translation}, rotation error {delta.Rotation}");
    }
}
