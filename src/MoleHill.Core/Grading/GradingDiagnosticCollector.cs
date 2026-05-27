namespace MoleHill.Core.Grading;

internal sealed class GradingDiagnosticCollector
{
    private readonly List<GradingDiagnostic> _diagnostics = new();

    public int Count => _diagnostics.Count;

    public void Add(GradingDiagnostic diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.Message))
            return;

        _diagnostics.Add(diagnostic);
    }

    public void AddInformation(string code, string message, string? operation = null, int? targetIndex = null)
    {
        Add(GradingDiagnostic.Information(code, message, operation, targetIndex));
    }

    public void AddWarning(string code, string message, string? operation = null, int? targetIndex = null)
    {
        Add(GradingDiagnostic.Warning(code, message, operation, targetIndex));
    }

    public void AddLegacyInformation(string message, string? operation = null, int? targetIndex = null)
    {
        AddInformation("legacy.message", message, operation, targetIndex);
    }

    public string[] ToMessages()
    {
        if (_diagnostics.Count == 0)
            return Array.Empty<string>();

        return _diagnostics.Select(static diagnostic => diagnostic.Message).ToArray();
    }

    public GradingDiagnostic[] ToStructuredDiagnostics()
    {
        return _diagnostics.Count == 0 ? Array.Empty<GradingDiagnostic>() : _diagnostics.ToArray();
    }
}
