using Accessibility.Application.AI;
using Accessibility.Application.Common.Interfaces;
using Accessibility.Infrastructure.Background;
using Accessibility.Infrastructure.Consumers;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<IApplicationDbContext, AccessibilityDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=accessibility;Username=postgres;Password=postgres"));
builder.Services.AddScoped(sp => (AccessibilityDbContext)sp.GetRequiredService<IApplicationDbContext>());
builder.Services.AddSingleton<IConversationAgent>(sp =>
{
    var provider = builder.Configuration["AI_PROVIDER"];
    return string.Equals(provider, "groq", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<GroqConversationAgent>(sp)
        : new FakeConversationAgent();
});
builder.Services.AddSingleton<IEmailSender, SendGridEmailSender>();
builder.Services.AddHttpClient();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TranscriptReceivedConsumer>();
    x.AddConsumer<AgentAnalysisConsumer>();
    x.AddConsumer<ConfirmedActionRouterConsumer>();
    x.AddConsumer<CreateNoteConsumer>();
    x.AddConsumer<CreateTaskConsumer>();
    x.AddConsumer<CreateAppointmentConsumer>();
    x.AddConsumer<ScheduleReminderConsumer>();
    x.AddConsumer<ReminderDueConsumer>();
    x.AddConsumer<SendEmailConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RABBITMQ_HOST"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RABBITMQ_USERNAME"] ?? "guest");
            h.Password(builder.Configuration["RABBITMQ_PASSWORD"] ?? "guest");
        });

        cfg.UseMessageRetry(r => r.Immediate(3));
        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)));
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<ReminderScheduler>();

builder.Services.AddSerilog(loggerConfiguration => loggerConfiguration
    .Enrich.FromLogContext()
    .WriteTo.Console());

var host = builder.Build();
host.Run();
