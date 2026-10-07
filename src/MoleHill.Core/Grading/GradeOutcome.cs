using System.Diagnostics.CodeAnalysis;

namespace MoleHill.Core.Grading;

/// <summary>
/// The result of a Grade Pad or Grade Path run: the graded terrain, or why there is none. Both graders
/// report failure the same way, so a host never has to know which one it called to explain a failure.
/// </summary>
public sealed class GradeOutcome
{
    public GradingResult? Result { get; init; }

    /// <summary>Why grading failed, or a non-fatal note when it succeeded.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>On failure, the boundaries the grader worked from, so a host can show where it gave up.</summary>
    public IReadOnlyList<OutputPolyline> FailureOutputPolylines { get; init; } = Array.Empty<OutputPolyline>();

    /// <summary>On failure, the structured reason (code, severity, location).</summary>
    public IReadOnlyList<GradingDiagnostic> FailureDiagnostics { get; init; } = Array.Empty<GradingDiagnostic>();

    [MemberNotNullWhen(true, nameof(Result))]
    public bool Succeeded => Result != null;
}
