using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class TangentMateHandler : MateHandler {
    protected override string WireType => "MateTangent";
    protected override swMateType_e NativeType => swMateType_e.swMateTANGENT;
    protected override IReadOnlyCollection<string> Options => ["alignment", "pick_points"];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        var mate = (ITangentMateFeatureData)data;
        mate.EntitiesToMate = ResolveEntities(file, input);
        mate.MateAlignment = ReadAlignment(input);
        if (creating && ReadPickPoints(file, input) is { } picks) mate.PickPoints = picks;
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        var mate = (ITangentMateFeatureData)data;
        result["alignment"] = AlignmentName(mate.MateAlignment);
        result["pick_points"] = InspectPickPoints(file, entities, mate.PickPoints);
    }
}
