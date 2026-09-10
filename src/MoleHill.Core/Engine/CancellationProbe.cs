namespace MoleHill.Core.Engine;

/// <summary>
/// Cooperative cancellation for the heavy Core stages, so a superseded build stops instead of running
/// its remaining rounds to completion while holding whole-mesh buffers.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation is reported by throwing <see cref="OperationCanceledException"/>, which is the contract
/// the Rhino build pipeline already uses. That matters more than a return code here: a stage abandoned
/// part-way has produced no valid output, and throwing makes it impossible for a caller to publish a
/// half-finished result to a cache by mistake.
/// </para>
/// <para>
/// <see cref="ThrowIfCancelledOften"/> only consults the callback every <see cref="DefaultInterval"/>
/// calls, so it is cheap enough to sit inside a per-face or per-vertex loop. Its counter is not
/// synchronised: shared between parallel workers it only makes checks more or less frequent, never
/// incorrect. Use <see cref="ThrowIfCancelled"/> at phase and round boundaries, where the check is
/// already rare.
/// </para>
/// </remarks>
public sealed class CancellationProbe
{
    /// <summary>Loop iterations between two consultations of the callback.</summary>
    public const int DefaultInterval = 4096;

    /// <summary>A probe that never cancels. Shared; holds no state that matters.</summary>
    public static readonly CancellationProbe None = new(null);

    private readonly Func<bool>? _shouldCancel;
    private readonly int _interval;
    private int _countdown;

    public CancellationProbe(Func<bool>? shouldCancel, int interval = DefaultInterval)
    {
        _shouldCancel = shouldCancel;
        _interval = Math.Max(1, interval);
        _countdown = _interval;
    }

    /// <summary>True when this probe can ever cancel; lets a caller skip setting up bookkeeping.</summary>
    public bool CanCancel => _shouldCancel != null;

    /// <summary>Creates a probe, or <see cref="None"/> when there is nothing to observe.</summary>
    public static CancellationProbe For(Func<bool>? shouldCancel)
    {
        return shouldCancel == null ? None : new CancellationProbe(shouldCancel);
    }

    /// <summary>Consults the callback now. For phase and round boundaries.</summary>
    public void ThrowIfCancelled()
    {
        if (_shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Cancelled.");
    }

    /// <summary>Consults the callback every <see cref="DefaultInterval"/> calls. For inner loops.</summary>
    public void ThrowIfCancelledOften()
    {
        if (_shouldCancel == null)
            return;

        if (--_countdown > 0)
            return;

        _countdown = _interval;
        if (_shouldCancel())
            throw new OperationCanceledException("Cancelled.");
    }
}
