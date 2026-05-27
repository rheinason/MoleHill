using System;
using System.Threading;
using System.Threading.Tasks;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainWorkerCancellationTests
{
    [Fact]
    public async Task CancelAndWaitForWorkers_CancelsAndDrainsResponsiveWorker()
    {
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task worker = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        bool completed = TerrainWorkerCancellation.CancelAndWaitForWorkers(
            new (CancellationTokenSource? Cancellation, Task? Task)[] { (cancellation, worker) },
            TimeSpan.FromSeconds(1));

        Assert.True(completed);
        await worker;
    }

    [Fact]
    public async Task CancelAndWaitForWorkers_ReturnsFalseWhenWorkerIgnoresCancellation()
    {
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = completion.Task;

        bool completed = TerrainWorkerCancellation.CancelAndWaitForWorkers(
            new (CancellationTokenSource? Cancellation, Task? Task)[] { (cancellation, worker) },
            TimeSpan.FromMilliseconds(50));

        Assert.False(completed);

        completion.SetResult(true);
        await worker;
    }
}
