using Accessibility.Api.Extensions;
using Accessibility.Application.Common.Interfaces;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

builder.Services.AddAccessibilityServices(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = (AccessibilityDbContext)scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
    await dbContext.Database.MigrateAsync();

    if (app.Configuration.GetValue("DEMO_SEED_ENABLED", true))
    {
        await DemoDataSeeder.SeedAsync(dbContext);
    }
}

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAccessibilityMiddleware();

app.Run();
