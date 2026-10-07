namespace MoleHill.Rhino.Services;

/// <summary>
/// Cancellation check shared by the stage classes. Same body as
/// <c>TerrainBuildService.ThrowIfCancellationRequested</c> (Analysis.cs); fold the two together once that file
/// is free to touch.
/// </summary>
internal static class StageSupport
{
    internal static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Terrain rebuild cancelled.");
    }
}
