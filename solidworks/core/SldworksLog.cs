using System.Text.Json;
using Serilog;
using Serilog.Core;

namespace Sldworks.Core;

public enum LoggingLevel {
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
    None = 6
}

public class LoggingSettings {
    public required LoggingLevel NormalLevel { get; init; }
    public required LoggingLevel StackTraceLevel { get; init; }
    public required LoggingLevel CrashOn { get; init; }

    public static LoggingSettings NormalDeveloper() {
        return new LoggingSettings {
            NormalLevel = LoggingLevel.Information,
            StackTraceLevel = LoggingLevel.Error,
            CrashOn = LoggingLevel.Fatal,
        };
    }

    public static LoggingSettings User() {
        return new LoggingSettings {
            NormalLevel = LoggingLevel.Error,
            StackTraceLevel = LoggingLevel.None,
            CrashOn = LoggingLevel.Fatal,
        };
    }
}

public static class SldworksLog {
    public static LoggingSettings Settings { get; set; } = LoggingSettings.User();

    public sealed record LoggerOptions(
        string FilePath,
        bool EnableFile = true
    );

    public static Serilog.Events.LogEventLevel ToSerilogLevel(LoggingLevel level) => level switch {
        LoggingLevel.Verbose => Serilog.Events.LogEventLevel.Verbose,
        LoggingLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LoggingLevel.Information => Serilog.Events.LogEventLevel.Information,
        LoggingLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LoggingLevel.Error => Serilog.Events.LogEventLevel.Error,
        LoggingLevel.Fatal => Serilog.Events.LogEventLevel.Fatal,
        _ => Serilog.Events.LogEventLevel.Verbose
    };

    public static void Configure(LoggerOptions options) {
        Serilog.Debugging.SelfLog.Enable(msg => {
            var selfLogPath = Path.Combine(Path.GetDirectoryName(options.FilePath)!, "serilog-selflog.txt");
            // Sldworks.Core.File shadows System.IO.File.
            System.IO.File.AppendAllText(selfLogPath, msg + Environment.NewLine);
        });

        var loggerConfig = new LoggerConfiguration().MinimumLevel.Verbose();

        if (options.EnableFile) {
            loggerConfig = loggerConfig.WriteTo.File(
                options.FilePath,
                rollingInterval: RollingInterval.Infinite,
                shared: true,
                buffered: false,
                restrictedToMinimumLevel: ToSerilogLevel(Settings.NormalLevel)
            );
        }

        Log.Logger = loggerConfig.CreateLogger();
        Information("Logger configured. Settings: {Settings}", Settings);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static void Verbose(string messageTemplate, params object?[]? propertyValues) {
        RunUsingLevel(LoggingLevel.Verbose, Log.Verbose, messageTemplate, propertyValues);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static void Debug(string messageTemplate, params object?[]? propertyValues) {
        RunUsingLevel(LoggingLevel.Debug, Log.Debug, messageTemplate, propertyValues);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static void Information(string messageTemplate, params object?[]? propertyValues) {
        RunUsingLevel(LoggingLevel.Information, Log.Information, messageTemplate, propertyValues);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static void Warning(string messageTemplate, params object?[]? propertyValues) {
        RunUsingLevel(LoggingLevel.Warning, Log.Warning, messageTemplate, propertyValues);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static void Error(string messageTemplate, params object?[]? propertyValues) {
        RunUsingLevel(LoggingLevel.Error, Log.Error, messageTemplate, propertyValues);
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    public static string Fatal(string messageTemplate, params object?[]? propertyValues) {
        return RunUsingLevel(LoggingLevel.Fatal, Log.Fatal, messageTemplate, propertyValues)!;
    }

    // Renders a Serilog template to a flat string; {@Name} JSON-serializes the value.
    private static readonly System.Text.RegularExpressions.Regex PlaceholderRegex =
        new(@"\{(@?)([a-zA-Z_][a-zA-Z0-9_]*)([^}]*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CustomFormat(string messageTemplate, object?[]? propertyValues) {
        propertyValues ??= [];

        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextIndex = 0;

        var numericTemplate = PlaceholderRegex.Replace(messageTemplate, match => {
            var serialize = match.Groups[1].Value.Length == 1;
            var name = match.Groups[2].Value;
            var alignmentOrFormat = match.Groups[3].Value;
            if (!nameToIndex.TryGetValue(name, out var idx)) {
                idx = nextIndex++;
                nameToIndex[name] = idx;
            }
            if (serialize && idx < propertyValues.Length) {
                propertyValues[idx] = JsonSerializer.Serialize(propertyValues[idx]);
            }
            return "{" + idx + alignmentOrFormat + "}";
        });

        return string.Format(numericTemplate, propertyValues);
    }

    private static string? RunUsingLevel(LoggingLevel logLevel, Action<string, object?[]?> logFunction, string messageTemplate, object?[]? propertyValues) {
        if (logLevel < Settings.NormalLevel) return null;

        propertyValues ??= [];
        for (var i = 0; i < propertyValues.Length; i++) {
            while (propertyValues[i] is Func<object?> valueFunc) {
                propertyValues[i] = valueFunc.Invoke();
            }
        }

        InvokeLogFunction(logFunction, messageTemplate, propertyValues);
        if (logLevel >= Settings.StackTraceLevel) {
            LogStackTrace(logFunction);
        }

        if (logLevel >= Settings.CrashOn && logLevel != LoggingLevel.Fatal) {
            throw new Exception(CustomFormat(messageTemplate, propertyValues));
        }

        return logLevel == LoggingLevel.Fatal ? CustomFormat(messageTemplate, propertyValues) : null;
    }

    private static void LogStackTrace(Action<string, object?[]?> logFunction) {
        foreach (var stackTraceLine in GetStackTrace().Split('\n')) {
            InvokeLogFunction(logFunction, "{frame}", [stackTraceLine]);
        }
    }

    private static void InvokeLogFunction(Action<string, object?[]?> logFunction, string messageTemplate, object?[]? propertyValues) {
        foreach (var templateLine in messageTemplate.Split('\n')) {
            logFunction.Invoke(templateLine, propertyValues);
        }
    }

    private static string GetStackTrace() {
        var stackTrace = new System.Diagnostics.StackTrace(true);
        var frames = stackTrace.GetFrames();
        var filteredFrames = frames
            .SkipWhile(f => f.GetMethod()?.DeclaringType == typeof(SldworksLog))
            .ToArray();
        return string.Join(
            '\n',
            filteredFrames.Select(f => f.ToString())
        );
    }
}
