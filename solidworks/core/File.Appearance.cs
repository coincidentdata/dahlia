using System.Text.Json;
using System.Text.Json.Nodes;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core;

public partial class File {
    private static double[]? ReadComponentColor(JsonNode? node) {
        if (node is null) return null;
        var color = node.Deserialize<double[]>();
        if (color is null || color.Length != 4 || color.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
            throw new ArgumentException("Component color requires four finite RGBA channels between 0 and 1");
        return color;
    }

    private static JsonNode? ComponentColor(Component2 component) =>
        component.MaterialPropertyValues is double[] values
            ? new JsonArray(values[0], values[1], values[2], 1 - values[7]) : null;

    private void SetComponentColor(Component2 component, double[]? color) {
        if (color is null) {
            if (component.MaterialPropertyValues is not null
                && !component.RemoveMaterialProperty2((int)swInConfigurationOpts_e.swThisConfiguration, null))
                throw new InvalidOperationException($"Could not clear color of '{component.Name2}'");
        } else {
            var values = component.MaterialPropertyValues as double[]
                ?? WithComponentSource(component, source => (double[])source.ModelDoc.MaterialPropertyValues);
            // Native RGB truncates to 8-bit buckets; use their centers so readback values replay stably.
            for (var i = 0; i < 3; i++) values[i] = Math.Min(1, (Math.Round(color[i] * 255) + 0.5) / 255);
            values[7] = 1 - color[3];
            component.SetMaterialPropertyValues2(values, (int)swInConfigurationOpts_e.swThisConfiguration, null);
        }
        _modelDoc.GraphicsRedraw2();
    }
}
