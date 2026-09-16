using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal abstract class MateHandler {
    private static readonly MateHandler[] Handlers = [
        new CoincidentMateHandler(), new ConcentricMateHandler(), new ParallelMateHandler(),
        new PerpendicularMateHandler(), new TangentMateHandler(), new DistanceMateHandler(),
        new AngleMateHandler(), new LockMateHandler(),
    ];

    protected abstract string WireType { get; }
    protected abstract swMateType_e NativeType { get; }
    protected abstract IReadOnlyCollection<string> Options { get; }
    protected abstract void Apply(File file, object data, JsonNode input, bool creating);
    protected abstract void InspectData(File file, object data, JsonObject result, List<Definition> entities);

    internal static string TypeName(int type) =>
        Handlers.FirstOrDefault(handler => (int)handler.NativeType == type)?.WireType ?? "UnsupportedMate";

    private static MateHandler ForType(string type) =>
        Handlers.FirstOrDefault(handler => handler.WireType == type)
        ?? throw new NotSupportedException($"Mate type '{type}' is not supported");

    private void ValidateInput(JsonNode input) {
        foreach (var (key, _) in input.AsObject()) {
            if (key is "type" or "name" or "entities" or "suppressed" or "echo_status" or "echo_dimension") continue;
            if (!Options.Contains(key)) throw new ArgumentException($"{WireType} does not support option '{key}'");
        }
    }

    internal static JsonNode Add(File file, JsonNode input) {
        var handler = ForType(input["type"]!.GetValue<string>());
        handler.ValidateInput(input);
        var suppressed = JsonHelpers.ReadBool(input, "suppressed", false);
        var data = file.AssemblyDoc.CreateMateData((int)handler.NativeType)
            ?? throw new InvalidOperationException($"Could not create data for {handler.WireType}");
        file.ModelDoc.ClearSelection2(true);
        try {
            handler.Apply(file, data, input, creating: true);
            var feature = file.AssemblyDoc.CreateMate(data) as Feature
                ?? throw new InvalidOperationException($"{handler.WireType} creation failed: native status {((IMateFeatureData)data).ErrorStatus}");
            File.ApplyFeatureName(feature, input);
            SetSuppressed(feature, suppressed);
            Verify(file, feature);
            return Inspect(file, feature);
        } finally { file.ModelDoc.ClearSelection2(true); }
    }

    internal static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var handler = ForType(input["type"]!.GetValue<string>());
        handler.ValidateInput(input);
        var suppressed = JsonHelpers.ReadBool(input, "suppressed", false);
        var before = Inspect(file, feature);
        if (handler.WireType != before["type"]!.GetValue<string>())
            throw new ArgumentException("Editing a mate cannot change its type");
        if (!SamePoints(input["pick_points"], before["pick_points"]))
            throw new NotSupportedException("Changing mate pick points requires a new mate");
        if (before["reference"] is not null && input["reference"] is null)
            throw new NotSupportedException("Removing an angle reference requires a new mate");
        var data = feature.GetDefinition();
        file.ModelDoc.ClearSelection2(true);
        try {
            handler.Apply(file, data, input, creating: false);
            if (!feature.ModifyDefinition(data, file.ModelDoc, null))
                throw new InvalidOperationException($"Could not edit mate '{feature.Name}'");
            SetSuppressed(feature, suppressed);
            Verify(file, feature);
            return Inspect(file, feature);
        } finally { file.ModelDoc.ClearSelection2(true); }
    }

    private static void Verify(File file, Feature feature) {
        file.RebuildModel();
        if (File.ReadFeatureStatus(feature) == "failed")
            throw new InvalidOperationException($"Mate '{feature.Name}' failed after rebuild: {feature.GetErrorCode2(out _)}");
    }

    private static void SetSuppressed(Feature feature, bool suppressed) {
        if ((File.ReadFeatureStatus(feature) == "suppressed") == suppressed) return;
        if (!feature.SetSuppression2(suppressed ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                : (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                (int)swInConfigurationOpts_e.swThisConfiguration, null))
            throw new InvalidOperationException($"Cannot change suppression of mate '{feature.Name}'");
    }

    protected static object MateEntity(File file, Definition definition) {
        var entity = definition.Resolve(file)
            ?? throw new ArgumentException($"Mate reference did not resolve: {definition.ToJson()}");
        if (entity is Component2 component && component.IsSuppressed())
            throw new InvalidOperationException($"Component '{component.Name2}' is suppressed; unsuppress it before mating it");
        // Keep datum Feature proxies in their assembly occurrence context.
        return entity;
    }

    protected static DispatchWrapper[] ResolveEntities(File file, JsonNode input) {
        // SolidWorks requires SAFEARRAY(IDispatch); object[] is accepted but drops the selections.
        return ReadEntities(input).Select(definition => new DispatchWrapper(MateEntity(file, definition))).ToArray();
    }

    private static List<Definition> ReadEntities(JsonNode input) {
        var entities = Definition.FromJsonArray(input["entities"], "Mate entities");
        if (entities.Count != 2) throw new ArgumentException("Mate requires exactly two references");
        return entities;
    }

    protected static double[]? ReadPickPoints(File file, JsonNode input) {
        var points = input["pick_points"]?.Deserialize<Point3D[]>(Definition.JsonOptions);
        if (points is null) return null;
        if (points.Length != 2 || points.Any(point => point is null
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)))
            throw new ArgumentException("Mate pick_points must contain two finite points");
        return TransformPickPoints(file, ReadEntities(input), points, inverse: false);
    }

    internal static JsonNode Inspect(File file, Feature feature) {
        _ = file.AssemblyDoc;
        var mate = feature.GetSpecificFeature2() as Mate2
            ?? throw new ArgumentException($"'{feature.Name}' is not a mate");
        var type = TypeName(mate.Type);
        var handler = ForType(type);
        var entities = new List<Definition>();
        for (var i = 0; i < mate.GetMateEntityCount(); i++) {
            var entity = mate.MateEntity(i);
            var component = entity.ReferenceComponent;
            if (component is not null && component.IsSuppressed())
                throw new InvalidOperationException($"Mate '{feature.Name}' references suppressed component '{component.Name2}'; unsuppress the component before inspecting or editing the mate");
            entities.Add(file.CaptureAssemblyEntity(entity.Reference, component));
        }
        var result = new JsonObject {
            ["type"] = type, ["name"] = feature.Name,
            ["suppressed"] = File.ReadFeatureStatus(feature) == "suppressed",
            ["echo_status"] = File.ReadFeatureStatus(feature),
            ["echo_dimension"] = mate.DisplayDimension2[0] is DisplayDimension display
                ? display.GetDimension2(0).FullName : null,
        };
        handler.InspectData(file, feature.GetDefinition(), result, entities);
        if (entities.Count != 2) throw new NotSupportedException($"{type} '{feature.Name}' has {entities.Count} entities; expected two");
        result["entities"] = new JsonArray(entities.Select(entity => entity.ToJson()).ToArray());
        return result;
    }

    protected static JsonNode? InspectPickPoints(File file, IReadOnlyList<Definition> entities, object? picks) {
        if (picks is null) return null;
        if (picks is not double[] values) throw new InvalidOperationException("Native mate pick points are not a double array");
        if (values.Length == 0) return null;
        if (values.Length != 6) throw new InvalidOperationException($"Unexpected pick point count: {values.Length}");
        var points = new[] { new Point3D(values[0], values[1], values[2]), new Point3D(values[3], values[4], values[5]) };
        var local = TransformPickPoints(file, entities, points, inverse: true);
        return JsonSerializer.SerializeToNode(new[] {
            new Point3D(local[0], local[1], local[2]), new Point3D(local[3], local[4], local[5]),
        });
    }

    private static double[] TransformPickPoints(File file, IReadOnlyList<Definition> entities, IReadOnlyList<Point3D> points, bool inverse) {
        var result = new double[6];
        for (var i = 0; i < 2; i++) {
            var point = points[i];
            var values = new[] { point.X, point.Y, point.Z };
            if (entities[i] is ComponentEntityDefinition scoped) {
                var transform = file.ResolveComponent(scoped.Component).Transform2;
                if (inverse) transform = transform.IInverse();
                values = (double[])((MathPoint)((MathPoint)file.MathUtil.CreatePoint(values)).MultiplyTransform(transform)).ArrayData;
            }
            Array.Copy(values, 0, result, i*3, 3);
        }
        return result;
    }

    private static bool SamePoints(JsonNode? a, JsonNode? b) {
        if (a is null || b is null) return a is null && b is null;
        var first = a.Deserialize<Point3D[]>(Definition.JsonOptions)!;
        var second = b.Deserialize<Point3D[]>(Definition.JsonOptions)!;
        return first.Length == second.Length && first.Zip(second).All(pair =>
            Math.Abs(pair.First.X - pair.Second.X) <= Flags.GeometryTolerance
            && Math.Abs(pair.First.Y - pair.Second.Y) <= Flags.GeometryTolerance
            && Math.Abs(pair.First.Z - pair.Second.Z) <= Flags.GeometryTolerance);
    }

    protected static string AlignmentName(int value) => value switch {
        (int)swMateAlign_e.swMateAlignALIGNED => "aligned",
        (int)swMateAlign_e.swMateAlignANTI_ALIGNED => "opposed",
        (int)swMateAlign_e.swMateAlignCLOSEST => "closest",
        _ => throw new NotSupportedException($"Unsupported mate alignment: {value}"),
    };

    protected static int ReadAlignment(JsonNode input) => (input["alignment"]?.GetValue<string>() ?? "closest") switch {
        "aligned" => (int)swMateAlign_e.swMateAlignALIGNED,
        "opposed" => (int)swMateAlign_e.swMateAlignANTI_ALIGNED,
        "closest" => (int)swMateAlign_e.swMateAlignCLOSEST,
        var unknown => throw new ArgumentException($"Unknown alignment '{unknown}'"),
    };

    protected static double ReadDimension(JsonNode input, string field) {
        var value = JsonHelpers.ReadDouble(input, field);
        if (!double.IsFinite(value) || value < 0) throw new ArgumentException("Mate dimension must be finite and nonnegative");
        return value;
    }

    protected static double[]? ReadLimits(JsonNode input, double value) {
        var limits = input["limits"]?.Deserialize<double[]>();
        if (limits is not null && (limits.Length != 2 || !limits.All(double.IsFinite)
            || limits[0] < 0 || limits[0] > value || value > limits[1]))
            throw new ArgumentException("Mate limits must be finite, nonnegative, and contain the initial value");
        return limits;
    }
}
