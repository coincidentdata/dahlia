using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Shared;

internal static class FeatureName {
    // SW quirk: when a sketch is reused by multiple features (one sketch driving
    // an extrude AND a cut-extrude, say), reading the sub-feature's `Feature.Name`
    // through the consumer returns the display name with a "<N>" suffix —
    // "Sketch5<2>" for the 2nd consumer of Sketch5. The suffix isn't a separate
    // feature; the canonical name (what walking IFirstFeature/IGetNextFeature
    // returns) is everything before the "<".
    //
    // Don't strip blindly: a feature could legitimately have "<N>" in its name.
    // Try the literal name first; only if no feature matches AND the name ends
    // with "<digits>", strip once and retry.
    private static readonly Regex SharedSuffix = new(@"<\d+>$", RegexOptions.Compiled);

    internal static string Resolve(File file, string name) {
        if (string.IsNullOrEmpty(name)) return name;
        if (FindByName(file, name) is not null) return name;
        var stripped = SharedSuffix.Replace(name, "");
        if (stripped == name) return name;
        return FindByName(file, stripped) is not null ? stripped : name;
    }

    private static Feature? FindByName(File file, string name) {
        var feat = file.ModelDoc.IFirstFeature();
        while (feat != null) {
            if (string.Equals(feat.Name, name, StringComparison.Ordinal)) return feat;
            feat = feat.IGetNextFeature();
        }
        return null;
    }
}
