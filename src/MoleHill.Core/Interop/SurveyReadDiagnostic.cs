namespace MoleHill.Core.Interop;

/// <summary>
/// One thing the reader could not do, tied to the line it happened on.
///
/// The line number is the point: "3 rows were malformed" is not actionable, and a survey file is
/// something the user can open in a text editor and fix.
/// </summary>
public readonly record struct SurveyReadDiagnostic(int LineNumber, string Message);
