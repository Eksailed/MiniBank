namespace MiniBank.Api.IntegrationTests;

internal static class ParallelRunner
{
    /// <summary>Запускает все действия, но не больше <paramref name="limit"/> одновременно.</summary>
    public static async Task<T[]> RunAsync<T>(IEnumerable<Func<Task<T>>> actions, int limit)
    {
        using var gate = new SemaphoreSlim(limit);
        var tasks = actions.Select(async action =>
        {
            await gate.WaitAsync();
            try
            {
                return await action();
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
    }
}
