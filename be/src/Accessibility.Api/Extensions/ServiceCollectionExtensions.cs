using Accessibility.Application.AI;
using Accessibility.Application.Common.Interfaces;
using Accessibility.Infrastructure.Background;
using Accessibility.Infrastructure.Consumers;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Services;
using AspNetCoreRateLimit;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;

namespace Accessibility.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAccessibilityServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<IApplicationDbContext, AccessibilityDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=accessibility;Username=postgres;Password=postgres"));

        services.AddSingleton<IConversationAgent, FakeConversationAgent>();
        services.AddSingleton<IEmailSender, SendGridEmailSender>();
        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RABBITMQ_HOST"] ?? "localhost", "/", h =>
                {
                    h.Username(configuration["RABBITMQ_USERNAME"] ?? "guest");
                    h.Password(configuration["RABBITMQ_PASSWORD"] ?? "guest");
                });
            });
        });

        var jwtSecret = configuration["JWT_SECRET"] ?? "local-secret";
        var jwtIssuer = configuration["JWT_ISSUER"] ?? "UavPms.Api";
        var jwtAudience = configuration["JWT_AUDIENCE"] ?? "UavPms.Client";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = true,
                    ValidIssuer = jwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = jwtAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        services.AddAuthorization();
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = false;
        });

        services.AddMemoryCache();
        services.Configure<IpRateLimitOptions>(configuration.GetSection("IpRateLimiting"));
        services.AddInMemoryRateLimiting();
        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

        services.AddHealthChecks();
        services.AddHttpClient();
        services.AddProblemDetails();
        return services;
    }

    public static WebApplication UseAccessibilityMiddleware(this WebApplication app)
    {
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();
        app.UseIpRateLimiting();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");
        app.MapHealthChecks("/health/live");
        return app;
    }
}
