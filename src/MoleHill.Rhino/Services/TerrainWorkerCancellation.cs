namespace MoleHill.Rhino.Services;

internal static class TerrainWorkerCancellation
{
    public static bool CancelAndWaitForWorkers(
        IEnumerable<(CancellationTokenSource? Cancellation, Task? Task)> workers,
        TimeSpan timeout)
    {
        var tasks = new List<Task>();
        var cancellations = new List<CancellationTokenSource>();
        foreach (var (cancellation, task) in workers)
        {
            if (cancellation != null)
            {
                cancellation.Cancel();
                cancellations.Add(cancellation);
            }

            if (task != null)
                tasks.Add(task);
        }

        bool allCompleted = true;
        if (tasks.Count > 0)
        {
            try
            {
                allCompleted = Task.WaitAll(tasks.ToArray(), timeout);
            }
            catch (AggregateException)
            {
                allCompleted = tasks.All(static task => task.IsCompleted);
            }
        }

        foreach (CancellationTokenSource cancellation in cancellations)
            cancellation.Dispose();

        return allCompleted;
    }
}
