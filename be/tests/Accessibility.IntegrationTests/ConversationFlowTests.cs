using Accessibility.Api.Controllers;
using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.Common.Models;
using Accessibility.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Accessibility.IntegrationTests;

public class ConversationFlowTests
{
    [Fact]
    public async Task CreateConversationAndSubmitTranscript_ReturnsCreatedConversation()
    {
        var services = new ServiceCollection();
        services.AddDbContext<IApplicationDbContext, AccessibilityDbContext>(options => options.UseInMemoryDatabase("conversation-flow"));
        services.AddScoped<ConversationsController>();
        services.AddSingleton<IPublishEndpoint>(Mock.Of<IPublishEndpoint>());

        using var provider = services.BuildServiceProvider();
        var controller = provider.GetRequiredService<ConversationsController>();

        var result = await controller.CreateConversation(new CreateConversationRequest("Test user"), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
    }
}
