/// <summary>Discovery is optional startup work and must not delay the listening socket.</summary>
internal sealed class ModelCatalogRefreshService(ModelCatalogService catalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        do
        {
            await catalog.RefreshAvailableModelsIfNeeded(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
