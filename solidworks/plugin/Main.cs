using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Serilog;
using Sldworks.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace Sldworks.Plugin;

public interface ISldworksPlugin {
    string RunCommand(string command, string parameters, int timeoutSeconds = 600);
}

[Guid("A0C98030-4177-44CC-BD4B-63A047C3F30A")]
[DisplayName("Dahlia for SOLIDWORKS")]
[Description("Python automation for SOLIDWORKS through Dahlia.")]
[ProgId("Sldworks.Plugin")]
[ComVisible(true)]
public class SldworksPlugin : SwAddin, ISldworksPlugin {
    private const string AddInKeyTemplate = @"SOFTWARE\SolidWorks\AddIns\{{{0}}}";
    private const string AddInStartupKeyTemplate = @"Software\SolidWorks\AddInsStartup\{{{0}}}";

    private SldWorks? _sldWorks;
    private Session? _session;
    private System.Windows.Forms.Control? _dispatcher;
    private readonly ConcurrentQueue<QueuedBatch> _commandQueue = new();
    private readonly string _logPath;

    public SldworksPlugin() {
        var dir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Sldworks");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "sldworks.log");
    }

    [ComRegisterFunction]
    public static void RegisterFunctions(Type t) {
        var title = t.GetCustomAttributes(false).OfType<DisplayNameAttribute>()
            .FirstOrDefault()?.DisplayName ?? t.ToString();
        var desc = t.GetCustomAttributes(false).OfType<DescriptionAttribute>()
            .FirstOrDefault()?.Description ?? t.ToString();

        using var addInKey = Registry.LocalMachine.CreateSubKey(
            string.Format(AddInKeyTemplate, t.GUID));
        addInKey.SetValue(null, 0);
        addInKey.SetValue("Title", title);
        addInKey.SetValue("Description", desc);

        using var startupKey = Registry.CurrentUser.CreateSubKey(
            string.Format(AddInStartupKeyTemplate, t.GUID));
        if (startupKey.GetValue(null) is null) {
            startupKey.SetValue(null, 1, RegistryValueKind.DWord);
        }
    }

    [ComUnregisterFunction]
    public static void UnregisterFunction(Type t) {
        Registry.LocalMachine.DeleteSubKey(string.Format(AddInKeyTemplate, t.GUID), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKey(string.Format(AddInStartupKeyTemplate, t.GUID), throwOnMissingSubKey: false);
    }

    public bool ConnectToSW(object? sldWorks, int cookie) {
        if (sldWorks is not SldWorks sw) {
            throw new ArgumentException("ConnectToSW requires a SldWorks COM object", nameof(sldWorks));
        }
        _sldWorks = sw;
        _sldWorks.SetAddinCallbackInfo2(0, this, cookie);
        _session = new Session(_sldWorks);
        _dispatcher = new System.Windows.Forms.Control();
        _ = _dispatcher.Handle;

        SldworksLog.Settings = LoggingSettings.NormalDeveloper();
        SldworksLog.Configure(new SldworksLog.LoggerOptions(FilePath: _logPath));
        SldworksLog.Information("Sldworks plugin connected");
        return true;
    }

    public bool DisconnectFromSW() {
        _dispatcher?.Dispose();
        _dispatcher = null;
        // SW quirk: two GC passes — finalizers can resurrect COM refs on the first pass.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return true;
    }

    public string RunCommand(string command, string parameters, int timeoutSeconds = 600) {
        if (_dispatcher is null) return Serialize(Error("SolidWorks plugin is not connected"));
        var parsed = JsonNode.Parse(parameters)!;
        var batch = parsed is JsonArray arr ? ParseBatch(arr) : [(command, parsed)];
        if (batch is null) {
            return Serialize(Error("Each batch item must have Command and Parameters"));
        }

        var promise = new TaskCompletionSource<JsonArray>();
        var cts = new CancellationTokenSource();
        _commandQueue.Enqueue(new QueuedBatch(batch, promise, cts));

        _dispatcher.BeginInvoke((Action)Process);

        if (!promise.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds))) {
            cts.Cancel();
            Log.Error("Command {Command} timed out after {Seconds}s", command, timeoutSeconds);
            return Serialize(Error($"Command '{command}' timed out after {timeoutSeconds} seconds"));
        }

        var results = promise.Task.Result;
        if (parsed is not JsonArray && results.Count == 1) {
            return Serialize(results[0]!);
        }
        return Serialize(results);
    }

    private static List<(string, JsonNode)>? ParseBatch(JsonArray arr) {
        var batch = new List<(string, JsonNode)>(arr.Count);
        foreach (var item in arr) {
            if (item is not JsonObject obj) return null;
            var name = obj["Command"]?.GetValue<string>();
            var args = obj["Parameters"];
            if (string.IsNullOrEmpty(name) || args is null) return null;
            batch.Add((name, args));
        }
        return batch;
    }

    private bool _processing;

    private void Process() {
        // Native property pages pump messages while a command is still running.
        if (_session is null || _processing) return;
        _processing = true;
        try {
            while (_commandQueue.TryDequeue(out var batch)) ProcessBatch(batch);
        } finally { _processing = false; }
    }

    private void ProcessBatch(QueuedBatch batch) {
        if (batch.Cancel.IsCancellationRequested) {
            batch.Cancel.Dispose();
            return;
        }
        var results = new JsonArray();
        // SW quirk: undocumented user-preference toggle 999999 suppresses modal
        // dialogs and feature-failure prompts that would otherwise block the
        // command queue waiting for a user click.
        _sldWorks?.SetUserPreferenceToggle(999999, true);
        try {
            foreach (var (name, args) in batch.Commands) {
                if (batch.Cancel.IsCancellationRequested) {
                    results.Add(Error("Batch cancelled"));
                    break;
                }
                results.Add(Dispatch(_session, name, args));
            }
        } finally {
            _sldWorks?.SetUserPreferenceToggle(999999, false);
        }
        batch.Promise.TrySetResult(results);
    }

    private static JsonNode Dispatch(Session session, string command, JsonNode args) {
        try {
            return command switch {
                "OpenFile"        => OpenFile(session, args),
                "CreateNewFile"   => CreateNewFile(session, args),
                "SaveFile"        => GetFile(session, args).Save(),
                "SaveFileAs"      => GetFile(session, args).Save(args["Path"]!.GetValue<string>()),
                "CloseFile"       => CloseFile(session, args),
                "CloseAllFiles"   => CloseAllFiles(session),
                "GetFiles"        => GetFiles(session),
                "GetActiveFile"   => GetActiveFile(session),
                "Shutdown"        => Shutdown(session),

                "ViewFile"        => GetFile(session, args).View(),
                "Rebuild"         => GetFile(session, args).Rebuild(),
                "AddComponent"    => GetFile(session, args).AddComponent(args["Component"]!),
                "GetComponents"   => GetFile(session, args).GetComponents(),
                "InspectComponent" => GetFile(session, args).InspectComponent(args["Component"]!),
                "EditComponent"   => GetFile(session, args).EditComponent(args["Component"]!, args["Changes"]!),
                "TranslateComponent" => GetFile(session, args).TranslateComponent(args["Component"]!, args["Delta"]!),
                "RotateComponent" => GetFile(session, args).RotateComponent(args["Component"]!, args["Axis"]!, args["Angle"]!.GetValue<double>()),
                "DeleteComponent" => GetFile(session, args).DeleteComponent(args["Component"]!),
                "ProbeComponent"  => GetFile(session, args).ProbeComponent(args["Component"]!, args["Ray"]!,
                                        args["EntityType"]!.GetValue<string>(), args["OnBody"]) ?? JsonValue.Create((string?)null)!,
                "GenerateComponentProbe" => GetFile(session, args).GenerateComponentProbe(args["Component"]!,
                                        args["Definition"]!, args["EntityType"]!.GetValue<string>(), args["StartAttempt"]?.GetValue<int>() ?? 0),
                "InspectFeature"  => GetFile(session, args).InspectFeature(args["Index"]!.GetValue<int>()),

                "Probe"           => Probe(session, args),
                "GenerateProbe"   => GenerateProbe(session, args),
                "ApproxMatchDef"  => ApproxMatchDef(args),
                "ProbeRegion"     => GetFile(session, args).ProbeRegion(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["X"]!.GetValue<double>(),
                                        args["Y"]!.GetValue<double>()),
                "DumpSelection"   => DumpSelection(session),

                "AddFeature"      => GetFile(session, args).AddFeature(args["Feature"]!),
                "EditFeature"     => GetFile(session, args).EditFeature(
                                        args["Name"]!.GetValue<string>(),
                                        args["Feature"]!),
                "GetDiscardProbes" => GetFile(session, args).GetDiscardProbes(
                                        args["FeatureName"]!.GetValue<string>()),
                "DropBodies"      => GetFile(session, args).DropBodies(
                                        args["FeatureName"]!.GetValue<string>(),
                                        args["Bodies"]!.AsArray()),
                "EditSplit"       => GetFile(session, args).EditSplit(
                                        args["FeatureName"]!.GetValue<string>(),
                                        args["ConsumeMarkedBodies"]!.GetValue<bool>(),
                                        // select_all → MarkedBodies may be empty/absent;
                                        // default to an empty array so the keep-all case
                                        // doesn't require a body list on the wire.
                                        args["MarkedBodies"]?.AsArray() ?? new JsonArray(),
                                        args["SelectAll"]!.GetValue<bool>(),
                                        args["MarkedBodyRays"]?.AsArray()),
                "AddSketchEntities" => GetFile(session, args).AddSketchEntities(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["Entities"]!.AsArray()),
                "AddConstraints"  => GetFile(session, args).AddConstraints(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["Constraints"]!.AsArray()),
                "EditSketchEntity" => GetFile(session, args).EditSketchEntity(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["Entity"]!),
                "DeleteSketchEntity" => GetFile(session, args).DeleteSketchEntity(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["Entity"]!),
                "EditDimension"   => GetFile(session, args).EditDimension(
                                        args["DimName"]!.GetValue<string>(),
                                        args["Value"]?.GetValue<double>(),
                                        args["NewName"]?.GetValue<string>()),
                "DeleteConstraint" => GetFile(session, args).DeleteConstraint(
                                        args["SketchName"]!.GetValue<string>(),
                                        args["Constraint"]!),
                "RenameFeature"   => GetFile(session, args).RenameFeature(
                                        args["Name"]!.GetValue<string>(),
                                        args["NewName"]!.GetValue<string>()),
                "DeleteFeature"   => GetFile(session, args).DeleteFeature(args["Index"]!.GetValue<int>()),
                "SetRollback"     => GetFile(session, args).SetRollback(args["Index"]!.GetValue<int>()),
                "SetBodies"       => GetFile(session, args).SetBodies(args["BodyIds"]!.AsArray()),
                "SetUnits"        => GetFile(session, args).SetUnits(args["Units"]!.GetValue<string>()),

                "GetImage"        => GetImage(session, args),

                "Lock"            => Lock(session, args),
                "Unlock"          => Unlock(session, args),

                _ => Error($"Unknown command: {command}"),
            };
        } catch (Exception e) {
            Log.Error(e, "Command {Command} threw", command);
            return Error(e.Message);
        }
    }

    private static Sldworks.Core.File GetFile(Session session, JsonNode args) =>
        session.GetFile(args["FileName"]!.GetValue<string>());

    private static JsonNode OpenFile(Session session, JsonNode args) {
        var f = session.OpenFile(args["Path"]!.GetValue<string>());
        return new JsonObject { ["FileName"] = f.Name, ["Path"] = f.Path };
    }

    private static JsonNode CreateNewFile(Session session, JsonNode args) {
        var f = session.CreateNewFile(args["Kind"]?.GetValue<string>() ?? "part");
        return new JsonObject { ["FileName"] = f.Name };
    }

    private static JsonNode CloseFile(Session session, JsonNode args) {
        GetFile(session, args).Close();
        return Ok();
    }

    private static JsonNode CloseAllFiles(Session session) {
        session.CloseAllFiles();
        return Ok();
    }

    private static JsonNode GetFiles(Session session) {
        var arr = new JsonArray();
        foreach (var f in session.ListFiles()) arr.Add(f.Name);
        return arr;
    }

    private static JsonNode GetActiveFile(Session session) {
        var f = session.GetActiveFile();
        if (f is null) return JsonValue.Create((string?)null)!;
        return new JsonObject { ["FileName"] = f.Name, ["Path"] = f.Path };
    }

    private static JsonNode Shutdown(Session session) {
        session.Shutdown();
        return Ok();
    }

    private static JsonNode Probe(Session session, JsonNode args) {
        var hit = GetFile(session, args).Probe(
            args["Ray"]!,
            args["EntityType"]!.GetValue<string>(),
            args["OnBody"]);
        return hit ?? JsonValue.Create((string?)null)!;
    }

    private static JsonNode GenerateProbe(Session session, JsonNode args) =>
        GetFile(session, args).GenerateProbe(
            args["Definition"]!,
            args["EntityType"]!.GetValue<string>(),
            args["StartAttempt"]?.GetValue<int>() ?? 0);

    // Two Definitions approx-match iff they describe the same entity within
    // Approx tolerance — see `DefinitionMatch.Approx`. The round-trip
    // runner uses this after `Probe(ray)` to validate the captured target
    // Def against the source-side Def; on mismatch it regenerates the ray
    // via `GenerateProbe(..., StartAttempt=N+1)` and retries. The author
    // surface (`File.probe`) doesn't see this — the user picks a ray and
    // trusts the hit; only the generation pipeline needs the match check.
    private static JsonNode ApproxMatchDef(JsonNode args) {
        var aDef = Sldworks.Core.Definitions.Definition.FromJson(args["A"]!)
            ?? throw new ArgumentException("ApproxMatchDef: A is not a Definition");
        var bDef = Sldworks.Core.Definitions.Definition.FromJson(args["B"]!)
            ?? throw new ArgumentException("ApproxMatchDef: B is not a Definition");
        return JsonValue.Create(
            Sldworks.Core.Definitions.DefinitionMatch.Approx(aDef, bDef));
    }

    // Diagnostic: snapshot SelectionMgr from the active file and return it as a JSON
    // array of "#i <type> mark=<m> <detail>" strings. No-op-safe (returns empty list
    // if no doc is active or nothing is selected). Useful for comparing what SW
    // selects via UI clicks vs what our handlers select via Select2/Select4.
    private static JsonNode DumpSelection(Session session) {
        var f = session.GetActiveFile();
        if (f is null) return new JsonArray();
        var entries = Sldworks.Core.Utils.SelectionDebug.Snapshot(f);
        var arr = new JsonArray();
        foreach (var e in entries) arr.Add(e);
        return arr;
    }

    private static JsonNode GetImage(Session session, JsonNode args) {
        var orientation = args["Orientation"]?.GetValue<string>() ?? "Isometric";
        var png = GetFile(session, args).GetImage(orientation);
        return new JsonObject { ["ImageBase64"] = Convert.ToBase64String(png) };
    }

    private static JsonNode Lock(Session session, JsonNode args) {
        GetFile(session, args).Lock();
        return Ok();
    }

    private static JsonNode Unlock(Session session, JsonNode args) {
        GetFile(session, args).Unlock();
        return Ok();
    }

    private static JsonObject Ok() => new() { ["ok"] = true };
    private static JsonObject Error(string message) => new() { ["Error"] = message };
    // null-safe: a command that legitimately returns C# null (e.g. Select on a
    // ray miss, where the core returns null to signal "nothing hit") serializes
    // to the JSON literal "null", which Python's json.loads decodes back to None.
    // Without this, `node.ToJsonString()` on a null `node` NREs and the wire
    // surfaces a generic "Object reference not set" instead of a clean None.
    private static string Serialize(JsonNode? node) => node?.ToJsonString() ?? "null";

    private readonly record struct QueuedBatch(
        List<(string Name, JsonNode Args)> Commands,
        TaskCompletionSource<JsonArray> Promise,
        CancellationTokenSource Cancel);
}
