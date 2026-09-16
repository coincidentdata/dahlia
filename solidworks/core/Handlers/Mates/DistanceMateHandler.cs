using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class DistanceMateHandler : MateHandler {
    protected override string WireType => "MateDistance";
    protected override swMateType_e NativeType => swMateType_e.swMateDISTANCE;
    protected override IReadOnlyCollection<string> Options => ["distance", "limits", "alignment", "flipped"];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        var mate = (IDistanceMateFeatureData)data;
        var distance = ReadDimension(input, "distance");
        var limits = ReadLimits(input, distance);
        mate.EntitiesToMate = ResolveEntities(file, input);
        mate.IsAdvancedMate = limits is not null;
        if (limits is not null) {
            mate.MinimumDistance = limits[0];
            mate.MaximumDistance = limits[1];
        }
        mate.Distance = distance;
        mate.MateAlignment = ReadAlignment(input);
        mate.FlipDimension = JsonHelpers.ReadBool(input, "flipped", false);
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        var mate = (IDistanceMateFeatureData)data;
        result["alignment"] = AlignmentName(mate.MateAlignment);
        result["distance"] = mate.Distance;
        result["flipped"] = mate.FlipDimension;
        result["limits"] = mate.IsAdvancedMate ? new JsonArray(mate.MinimumDistance, mate.MaximumDistance) : null;
    }
}
