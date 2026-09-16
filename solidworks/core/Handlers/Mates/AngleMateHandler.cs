using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class AngleMateHandler : MateHandler {
    protected override string WireType => "MateAngle";
    protected override swMateType_e NativeType => swMateType_e.swMateANGLE;
    protected override IReadOnlyCollection<string> Options => ["angle", "limits", "alignment", "flipped", "reference"];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        var mate = (IAngleMateFeatureData)data;
        var angle = ReadDimension(input, "angle");
        var limits = ReadLimits(input, angle);
        var reference = input["reference"] is null ? null : Definition.FromJson(input["reference"])
            ?? throw new ArgumentException("Malformed angle reference");
        mate.EntitiesToMate = ResolveEntities(file, input);
        mate.IsAdvancedMate = limits is not null;
        if (limits is not null) {
            mate.MinimumAngle = limits[0];
            mate.MaximumAngle = limits[1];
        }
        if (reference is not null) mate.ReferenceEntity = MateEntity(file, reference);
        mate.Angle = angle;
        mate.MateAlignment = ReadAlignment(input);
        mate.FlipDimension = JsonHelpers.ReadBool(input, "flipped", false);
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        var mate = (IAngleMateFeatureData)data;
        result["alignment"] = AlignmentName(mate.MateAlignment);
        result["angle"] = mate.Angle;
        result["flipped"] = mate.FlipDimension;
        result["limits"] = mate.IsAdvancedMate ? new JsonArray(mate.MinimumAngle, mate.MaximumAngle) : null;
        result["reference"] = entities.Count == 3 ? entities[2].ToJson() : null;
        if (entities.Count == 3) entities.RemoveAt(2);
    }
}
