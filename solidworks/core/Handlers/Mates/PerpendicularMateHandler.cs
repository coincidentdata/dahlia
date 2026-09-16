using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class PerpendicularMateHandler : MateHandler {
    protected override string WireType => "MatePerpendicular";
    protected override swMateType_e NativeType => swMateType_e.swMatePERPENDICULAR;
    protected override IReadOnlyCollection<string> Options => ["pick_points"];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        var mate = (IPerpendicularMateFeatureData)data;
        mate.EntitiesToMate = ResolveEntities(file, input);
        if (creating && ReadPickPoints(file, input) is { } picks) mate.PickPoints = picks;
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        var mate = (IPerpendicularMateFeatureData)data;
        result["pick_points"] = InspectPickPoints(file, entities, mate.PickPoints);
    }
}
