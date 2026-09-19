using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// R09. The opt-in gate every benchmark shares, so a performance run states what it actually executed.
///
/// A benchmark that returns early when <c>MOLEHILL_PERF</c> is absent still reports as a pass, which is
/// how a managed run's green total can silently include bodies that never ran. Routing every gate
/// through here means each benchmark emits one of two unambiguous lines — <c>BENCHMARK RAN</c> or
/// <c>BENCHMARK NOT RUN</c> — and the performance lane (<c>MOLEHILL_REQUIRE_PERF=1</c>) fails outright
/// rather than passing on skipped work.
/// </summary>
internal static class PerformanceLane
{
    internal const string RanPrefix = "BENCHMARK RAN: ";
    internal const string NotRunPrefix = "BENCHMARK NOT RUN: ";

    /// <summary>Opt in with <c>MOLEHILL_PERF=1</c>.</summary>
    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal);

    /// <summary>
    /// <c>MOLEHILL_REQUIRE_PERF=1</c> marks a run as the performance lane: a benchmark that does not
    /// execute is a failed lane, not a quiet pass.
    /// </summary>
    public static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_REQUIRE_PERF"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Call at the top of a benchmark: <c>if (!PerformanceLane.ShouldRun(output, "zone splitter scaling")) return;</c>.
    /// Writes the executed/not-executed line and, in the performance lane, fails when the body would be skipped.
    /// </summary>
    public static bool ShouldRun(ITestOutputHelper output, string what)
    {
        if (IsEnabled)
        {
            output.WriteLine(RanPrefix + what);
            return true;
        }

        Assert.False(
            IsRequired,
            $"MOLEHILL_REQUIRE_PERF=1 but MOLEHILL_PERF is not set, so '{what}' would not execute. " +
            "The performance lane must run its benchmark bodies; set MOLEHILL_PERF=1.");

        output.WriteLine(NotRunPrefix + what + " (set MOLEHILL_PERF=1 to run it).");
        return false;
    }
}
