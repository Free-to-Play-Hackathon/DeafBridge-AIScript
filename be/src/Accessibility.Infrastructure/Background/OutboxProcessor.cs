using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Accessibility.Infrastructure.Background;

public class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

                var pendingMessages = await ((DbContext)dbContext).Set<OutboxMessage>()
                    .Where(x => x.ProcessedAt == null)
                    .OrderBy(x => x.OccurredAt)
                    .Take(20)
                    .ToListAsync(stoppingToken);

                foreach (var message in pendingMessages)
                {
                    try
                    {
                        var eventType = Type.GetType(message.Type);
                        if (eventType is null)
                        {
                            message.Error = $"Type {message.Type} not found";
                            message.RetryCount += 1;
                            await dbContext.SaveChangesAsync(stoppingToken);
                            continue;
                        }

                        var payload = JsonSerializer.Deserialize(message.Payload, eventType);
                        if (payload is null)
                        {
                            message.Error = "Failed to deserialize payload";
                            message.RetryCount += 1;
                            await dbContext.SaveChangesAsync(stoppingToken);
                            continue;
                        }

                        await publishEndpoint.Publish(payload, stoppingToken);
                        message.ProcessedAt = DateTimeOffset.UtcNow;
                        message.Error = null;
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        message.RetryCount += 1;
                        message.Error = ex.Message;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogError(ex, "Failed to process outbox message {MessageId}", message.Id);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Outbox processor waiting for database readiness");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
