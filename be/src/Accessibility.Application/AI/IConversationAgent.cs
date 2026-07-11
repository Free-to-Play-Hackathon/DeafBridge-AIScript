using Accessibility.Application.AI.Models;

namespace Accessibility.Application.AI;

public interface IConversationAgent
{
    Task<AgentResult> AnalyzeAsync(
        AgentContext context,
        CancellationToken cancellationToken);
}
