namespace MoleHill.Core.Grading;

public enum GradingDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public readonly record struct GradingDiagnostic(
    GradingDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Operation = null,
    int? TargetIndex = null,
    double? X = null,
    double? Y = null)
{
    public static GradingDiagnostic Information(string code, string message, string? operation = null, int? targetIndex = null)
    {
        return new GradingDiagnostic(GradingDiagnosticSeverity.Information, code, message, operation, targetIndex);
    }

    public static GradingDiagnostic Warning(string code, string message, string? operation = null, int? targetIndex = null)
    {
        return new GradingDiagnostic(GradingDiagnosticSeverity.Warning, code, message, operation, targetIndex);
    }

    public static GradingDiagnostic Error(string code, string message, string? operation = null, int? targetIndex = null)
    {
        return new GradingDiagnostic(GradingDiagnosticSeverity.Error, code, message, operation, targetIndex);
    }

    public override string ToString()
    {
        return Message;
    }

    internal static IReadOnlyList<GradingDiagnostic> FromLegacyMessages(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
            return Array.Empty<GradingDiagnostic>();

        var diagnostics = new GradingDiagnostic[messages.Count];
        for (int i = 0; i < messages.Count; i++)
            diagnostics[i] = Information("legacy.message", messages[i]);

        return diagnostics;
    }
}
