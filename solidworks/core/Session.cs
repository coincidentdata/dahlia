using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core;

public class Session {
    public SldWorks SldWorks { get; }

    // Lock and pending body-selection state belong to the document, even after SaveAs.
    private readonly Dictionary<ModelDoc2, File> _files = new();

    public Session(SldWorks sldWorks) {
        SldWorks = sldWorks;
    }

    public File OpenFile(string path) => OpenFile(path, background: false);

    internal File OpenFile(string path, bool background) {
        path = System.IO.Path.GetFullPath(path);
        var documentType = System.IO.Path.GetExtension(path).ToLowerInvariant() switch {
            ".sldprt" => swDocumentTypes_e.swDocPART,
            ".sldasm" => swDocumentTypes_e.swDocASSEMBLY,
            _ => throw new ArgumentException("OpenFile requires a .SLDPRT or .SLDASM file"),
        };
        if (background && SldWorks.GetOpenDocumentByName(path) is ModelDoc2 loaded)
            return FindFile(loaded.GetTitle());
        // SW quirk: OpenDoc6 reports failures via bitmask out-params, not exceptions.
        // NOTE: neutral CAD import (STEP/IGES) is NOT done here — LoadFile4's import UI
        // deadlocks when called on the add-in's RPC worker thread (off SW's main thread).
        int errors = 0, warnings = 0;
        ModelDoc2? doc;
        var visible = SldWorks.GetDocumentVisible((int)documentType);
        try {
            if (background) SldWorks.DocumentVisible(false, (int)documentType);
            doc = SldWorks.OpenDoc6(
                path,
                (int)documentType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "",
                ref errors,
                ref warnings) as ModelDoc2;
        } finally {
            if (background) SldWorks.DocumentVisible(visible, (int)documentType);
        }

        if (doc is null || errors != 0) {
            var details = DecodeFileLoadResult(errors, warnings);
            throw new InvalidOperationException($"OpenDoc6 failed for '{path}': {details}");
        }
        if (warnings != 0) {
            SldworksLog.Warning("OpenDoc6 returned warnings for {Path}: {Details}", path, DecodeFileLoadResult(0, warnings));
        }

        if (!string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException($"OpenDoc6 opened '{doc.GetPathName()}' instead of '{path}'");
        }
        return FindFile(doc.GetTitle());
    }

    public File CreateNewFile(string kind = "part") {
        // Prefer the user's default-part template; fall back to INewPart() when the
        // configured template path is stale (common after SW major upgrades).
        var preference = kind switch {
            "part" => swUserPreferenceStringValue_e.swDefaultTemplatePart,
            "assembly" => swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
            _ => throw new ArgumentException("File kind must be 'part' or 'assembly'"),
        };
        var template = SldWorks.GetUserPreferenceStringValue((int)preference);
        ModelDoc2? doc = null;
        if (!string.IsNullOrEmpty(template) && System.IO.File.Exists(template)) {
            doc = SldWorks.NewDocument(template, 0, 0, 0) as ModelDoc2;
        }
        if (doc == null) {
            doc = (kind == "part" ? (object)SldWorks.INewPart() : SldWorks.INewAssembly()) as ModelDoc2
                ?? throw new InvalidOperationException(
                    $"CreateNewFile: could not create {kind} using template '{template}'");
        }

        SldworksLog.Information("CreateNewFile: created {Title}", doc.GetTitle());
        return FindFile(doc.GetTitle());
    }

    public IReadOnlyList<File> ListFiles() {
        var documents = OpenDocuments(SldWorks).ToHashSet();
        foreach (var doc in _files.Keys.Where(doc => !documents.Contains(doc)).ToList()) {
            _files.Remove(doc);
        }
        foreach (var doc in documents) {
            if (!_files.ContainsKey(doc)) _files.Add(doc, new File(doc, SldWorks));
        }
        return _files.Values.ToList();
    }

    public File? GetActiveFile() {
        if (SldWorks.ActiveDoc is not ModelDoc2 active) return null;

        return FindFile(active.GetTitle());
    }

    public File GetFile(string fileName) {
        var file = FindFile(fileName);

        // SW quirk: ActivateDoc3 reports failure via bitmask out-param.
        int activateErrors = 0;
        var activated = SldWorks.ActivateDoc3(
            file.Name,
            false,
            (int)swRebuildOnActivation_e.swRebuildActiveDoc,
            ref activateErrors) as ModelDoc2;
        if (activated is null) {
            var details = DecodeBitmask<swActivateDocError_e>(activateErrors);
            throw new InvalidOperationException($"ActivateDoc3 failed for {fileName}: {details}");
        }
        if (SldWorks.IsSame(activated, file.ModelDoc) != (int)swObjectEquality.swObjectSame) {
            throw new InvalidOperationException($"ActivateDoc3 selected a different document for '{fileName}'");
        }
        return file;
    }

    public void CloseAllFiles() {
        if (!SldWorks.CloseAllDocuments(true)) {
            throw new InvalidOperationException("CloseAllDocuments returned false");
        }
        _files.Clear();
        SldworksLog.Information("CloseAllFiles: ledger cleared");
    }

    public void Shutdown() {
        SldworksLog.Information("Shutdown: telling SolidWorks to exit");
        // SW quirk: ExitApp returns void and may not actually terminate the host process.
        SldWorks.ExitApp();
    }

    internal static IEnumerable<ModelDoc2> OpenDocuments(SldWorks sldWorks) =>
        ((object[]?)sldWorks.GetDocuments() ?? []).Cast<ModelDoc2>();

    private File FindFile(string name) {
        var matches = ListFiles().Where(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch {
            0 => throw new ArgumentException($"File not open: {name}"),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Ambiguous document name '{name}': {string.Join(", ", matches.Select(file => file.Path))}. Open document names must be unique."),
        };
    }

    // SW quirk: bit 0 (value=0) means "no error" and is skipped.
    private static string DecodeBitmask<TEnum>(int value) where TEnum : Enum {
        if (value == 0) return "";
        var names = new List<string>();
        foreach (TEnum entry in Enum.GetValues(typeof(TEnum))) {
            var bit = Convert.ToInt32(entry);
            if (bit != 0 && (value & bit) == bit) names.Add(entry.ToString()!);
        }
        return string.Join(", ", names);
    }

    private static string DecodeFileLoadResult(int errors, int warnings) {
        var parts = new List<string>();
        if (errors != 0) parts.Add($"errors=[{DecodeBitmask<swFileLoadError_e>(errors)}]");
        if (warnings != 0) parts.Add($"warnings=[{DecodeBitmask<swFileLoadWarning_e>(warnings)}]");
        return parts.Count == 0 ? "ok" : string.Join("; ", parts);
    }
}
