using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Shared;

internal static class ExtrudeEndCondition {
    internal enum Kind {
        Blind,
        MidPlane,
        ThroughAll,
        ThroughNext,
        UpToNext,
        UpToEdgeOrVertex,
        UpToSurface,
        UpToBody,
        OffFrom,
    }

    internal sealed record Payload(
        Kind Kind,
        double Distance,
        Definition? EndDefinition,
        bool OffsetReversed,
        bool TranslateSurface) {

        internal static Payload Blind(double depth) =>
            new(Kind.Blind, depth, null, false, false);

        internal static Payload? Parse(JsonNode? node) {
            if (node is null) return null;
            var name = node["end_condition"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) return null;
            return name switch {
                "Blind"            => new(Kind.Blind,            ReadDouble(node, "distance"), null,                                   false, false),
                "MidPlane"         => new(Kind.MidPlane,         ReadDouble(node, "distance"), null,                                   false, false),
                "ThroughAll"       => new(Kind.ThroughAll,       0.0, null,                                                            false, false),
                "ThroughNext"      => new(Kind.ThroughNext,      0.0, null,                                                            false, false),
                "UpToNext"         => new(Kind.UpToNext,         0.0, null,                                                            false, false),
                // "UpToVertex" is input-only alias; output uses "UpToEdgeOrVertex".
                "UpToEdgeOrVertex" => new(Kind.UpToEdgeOrVertex, 0.0, Definition.FromJson(node["end"]),                                false, false),
                "UpToVertex"       => new(Kind.UpToEdgeOrVertex, 0.0, Definition.FromJson(node["end"]),                                false, false),
                "UpToSurface"      => new(Kind.UpToSurface,      0.0, Definition.FromJson(node["end"]),                                false, false),
                "UpToBody"         => new(Kind.UpToBody,         0.0, Definition.FromJson(node["end"]),                                false, false),
                "OffFrom"          => ParseOffFrom(node),
                _ => throw new ArgumentException($"Unknown end_condition: {name}"),
            };
        }

        private static Payload ParseOffFrom(JsonNode node) {
            var surface = Definition.FromJson(node["surface"])
                ?? throw new ArgumentException("end_condition='OffFrom' requires a 'surface' Definition");
            var distance = ReadDouble(node, "distance");
            var reversed = node["reversed"]?.GetValue<bool>() ?? false;
            var translate = node["translate_surface"]?.GetValue<bool>() ?? false;
            return new Payload(Kind.OffFrom, distance, surface, reversed, translate);
        }
    }

    internal static (int endCode, double distance, Definition? endRef, bool offsetReverse, bool translateSurface) Translate(Payload payload) {
        return payload.Kind switch {
            Kind.Blind            => ((int)swEndConditions_e.swEndCondBlind,             payload.Distance, null,                       false,                  false),
            Kind.MidPlane         => ((int)swEndConditions_e.swEndCondMidPlane,          payload.Distance, null,                       false,                  false),
            Kind.ThroughAll       => ((int)swEndConditions_e.swEndCondThroughAll,        0.0,              null,                       false,                  false),
            Kind.ThroughNext      => ((int)swEndConditions_e.swEndCondThroughNext,       0.0,              null,                       false,                  false),
            Kind.UpToNext         => ((int)swEndConditions_e.swEndCondUpToNext,          0.0,              null,                       false,                  false),
            // SW quirk: swEndCondUpToVertex accepts both vertex and edge targets.
            Kind.UpToEdgeOrVertex => ((int)swEndConditions_e.swEndCondUpToVertex,        0.0,              payload.EndDefinition,      false,                  false),
            Kind.UpToSurface      => ((int)swEndConditions_e.swEndCondUpToSurface,       0.0,              payload.EndDefinition,      false,                  false),
            Kind.UpToBody         => ((int)swEndConditions_e.swEndCondUpToBody,          0.0,              payload.EndDefinition,      false,                  false),
            Kind.OffFrom          => ((int)swEndConditions_e.swEndCondOffsetFromSurface, payload.Distance, payload.EndDefinition,      payload.OffsetReversed, payload.TranslateSurface),
            _ => throw new ArgumentException($"Unknown end condition: {payload.Kind}"),
        };
    }

    // Sketch profile is at mark 0; end-condition reference goes on mark 1.
    internal static void AppendEndConditionSelection(File file, Definition? def) {
        if (def is null) return;
        if (!def.Select(file, 1)) {
            throw new InvalidOperationException(
                $"Extrude: UpTo*/OffFrom end-condition Definition ({def.GetType().Name}) did not resolve+select onto mark 1");
        }
    }

    internal static JsonNode CaptureEndCondition(IExtrudeFeatureData2 data, bool primary, string handlerLabel) {
        var code = data.GetEndCondition(primary);
        var depth = data.GetDepth(primary);
        switch (code) {
            case (int)swEndConditions_e.swEndCondBlind:
                return new JsonObject { ["end_condition"] = "Blind", ["distance"] = depth };
            case (int)swEndConditions_e.swEndCondMidPlane:
                return new JsonObject { ["end_condition"] = "MidPlane", ["distance"] = depth };
            case (int)swEndConditions_e.swEndCondThroughAll:
            case (int)swEndConditions_e.swEndCondThroughAllBoth:
                return new JsonObject { ["end_condition"] = "ThroughAll" };
            case (int)swEndConditions_e.swEndCondThroughNext:
                return new JsonObject { ["end_condition"] = "ThroughNext" };
            case (int)swEndConditions_e.swEndCondUpToNext:
                return new JsonObject { ["end_condition"] = "UpToNext" };
            case (int)swEndConditions_e.swEndCondUpToVertex:
                // SW quirk: swEndCondUpToVertex covers both vertex and edge targets.
                return new JsonObject {
                    ["end_condition"] = "UpToEdgeOrVertex",
                    ["end"] = CaptureEndConditionRef(data, primary),
                };
            case (int)swEndConditions_e.swEndCondUpToSurface:
            case (int)swEndConditions_e.swEndCondUpToSelection:
                return new JsonObject {
                    ["end_condition"] = "UpToSurface",
                    ["end"] = CaptureEndConditionRef(data, primary),
                };
            case (int)swEndConditions_e.swEndCondUpToBody:
                return new JsonObject {
                    ["end_condition"] = "UpToBody",
                    ["end"] = CaptureEndConditionRef(data, primary),
                };
            case (int)swEndConditions_e.swEndCondOffsetFromSurface:
                return new JsonObject {
                    ["end_condition"] = "OffFrom",
                    ["distance"] = depth,
                    ["reversed"] = data.GetReverseOffset(primary),
                    ["translate_surface"] = data.GetTranslateSurface(primary),
                    ["surface"] = CaptureEndConditionRef(data, primary),
                };
            default:
                throw new InvalidOperationException(
                    $"Inspect({handlerLabel}): end condition code {code} is not representable on the wire");
        }
    }

    internal static double SelectTopLevelDepth(IExtrudeFeatureData2 data) {
        var code = data.GetEndCondition(true);
        return code switch {
            (int)swEndConditions_e.swEndCondBlind    => data.GetDepth(true),
            (int)swEndConditions_e.swEndCondMidPlane => data.GetDepth(true),
            _ => 0.0,
        };
    }

    // Signed: negative = DraftOutward. SW quirk: GetDraftAngle is non-zero even when DraftWhileExtruding is off, so gate on the flag.
    internal static double CaptureDraftAngle(IExtrudeFeatureData2 data, bool primary) {
        if (!data.GetDraftWhileExtruding(primary)) return 0.0;
        var d = data.GetDraftAngle(primary);
        if (data.GetDraftOutward(primary)) d = -d;
        return d;
    }

    private static JsonNode CaptureEndConditionRef(IExtrudeFeatureData2 data, bool primary) {
        // SW quirk: GetEndConditionReference's out-param is a "type" enum — entity is the return value.
        var entity = data.GetEndConditionReference(primary, out _)
            ?? throw new InvalidOperationException(
                $"Inspect(Extrude): UpTo*/OffFrom reference is null on direction {(primary ? "1" : "2")}");
        return DefinitionCapture.Capture(entity)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Inspect(Extrude): UpTo*/OffFrom reference is unsupported runtime type {entity.GetType().Name}");
    }

    private static double ReadDouble(JsonNode node, string field) {
        var v = node[field] ?? throw new ArgumentException($"Missing required numeric field '{field}'");
        return v.GetValue<double>();
    }
}
