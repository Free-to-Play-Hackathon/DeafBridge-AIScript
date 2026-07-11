using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Worker;

public class Worker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    public Worker(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<AccessibilityDbContext>();
        await dbContext.Database.MigrateAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
