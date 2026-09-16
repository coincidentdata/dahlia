using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Mates;

internal sealed class LockMateHandler : MateHandler {
    protected override string WireType => "MateLock";
    protected override swMateType_e NativeType => swMateType_e.swMateLOCK;
    protected override IReadOnlyCollection<string> Options => [];

    protected override void Apply(File file, object data, JsonNode input, bool creating) {
        ((ILockMateFeatureData)data).EntitiesToMate = ResolveEntities(file, input);
    }

    protected override void InspectData(File file, object data, JsonObject result, List<Definition> entities) {
        _ = (ILockMateFeatureData)data;
    }
}
