namespace Sldworks.Core;

public static class Flags {
    // Geometric comparison tolerance for Definition matching, in meters.
    public const double GeometryTolerance = 1e-7;

    // Body mass properties drift across rebuilds for curved faces. Keep tolerances
    // separate by unit: centroid in meters, volume in cubic meters, area in square meters.
    public const double BodyCentroidTol = 1e-4;
    public const double BodyVolumeTol = 5e-8;
    public const double BodyAreaTol = 2e-5;

    // Widest of the three. Use ONLY where a single tolerance must cover a whole
    // body subtree generically (JsonNodesEqualWithinTol walks untyped nodes and
    // cannot tell a volume from a length); prefer the per-unit values above
    // anywhere the field is known.
    public const double BodyMassPropertyTol = BodyCentroidTol;

    public static LoggingSettings LoggingSettings {
        get => SldworksLog.Settings;
        set => SldworksLog.Settings = value;
    }
}
