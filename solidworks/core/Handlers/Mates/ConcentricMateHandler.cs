using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class ConcentricMateHandler : MateHandler {
    protected override string WireType => "MateConcentric";
    protected override swMateType_e NativeType => swMateType_e.swMateCONCENTRIC;
    protected override IReadOnlyCollection<string> Options => ["alignment", "pick_points", "lock_rotation"];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        var mate = (IConcentricMateFeatureData)data;
        var entities = ResolveEntities(file, input);
        if (entities.All(entity => entity.WrappedObject is Feature feature && feature.GetTypeName2() == "RefAxis"))
            throw new ArgumentException("Use mates.coincident(...) to align two reference axes; a concentric mate does not support this pair");
        mate.EntitiesToMate = entities;
        mate.MateAlignment = ReadAlignment(input);
        mate.LockRotation = JsonHelpers.ReadBool(input, "lock_rotation", false);
        if (creating && ReadPickPoints(file, input) is { } picks) mate.PickPoints = picks;
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        var mate = (IConcentricMateFeatureData)data;
        result["alignment"] = AlignmentName(mate.MateAlignment);
        result["lock_rotation"] = mate.LockRotation;
        result["pick_points"] = InspectPickPoints(file, entities, mate.PickPoints);
    }
}
