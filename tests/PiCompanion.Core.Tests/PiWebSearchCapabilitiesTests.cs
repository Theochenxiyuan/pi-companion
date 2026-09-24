using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class PiWebSearchCapabilitiesTests
{
    [Theory]
    [InlineData("openai/gpt-5.4", PiWebSearchIntegration.CompanionTool)]
    [InlineData("google/gemini-2.5-pro", PiWebSearchIntegration.CompanionTool)]
    [InlineData("anthropic/claude-sonnet-4", PiWebSearchIntegration.CompanionTool)]
    [InlineData("openai-codex/gpt-5.6", PiWebSearchIntegration.CompanionTool)]
    [InlineData("azure-openai-responses/gpt-5.4", PiWebSearchIntegration.CompanionTool)]
    [InlineData("xai/grok-4", PiWebSearchIntegration.CompanionTool)]
    [InlineData("github-copilot/gpt-5.6-sol", PiWebSearchIntegration.CompanionTool)]
    [InlineData("opencode/gpt-5.6-luna", PiWebSearchIntegration.CompanionTool)]
    [InlineData("opencode-go/grok-4.6", PiWebSearchIntegration.CompanionTool)]
    [InlineData("xiaomi/mimo-v2.5", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.5-pro", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.5-pro-ultraspeed", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.6-flash", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.6-pro", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.6-pro-ultraspeed", PiWebSearchIntegration.ProviderRequest)]
    [InlineData("xiaomi/mimo-v2.6-unknown", PiWebSearchIntegration.None)]
    [InlineData("xiaomi-token-plan-cn/mimo-v2.5-pro", PiWebSearchIntegration.None)]
    [InlineData("company-proxy/gpt-5.4", PiWebSearchIntegration.None)]
    [InlineData("Pi 默认模型", PiWebSearchIntegration.None)]
    public void ResolveModelReference_RestrictsSearchToApprovedOfficialProviders(
        string model,
        PiWebSearchIntegration expected)
    {
        Assert.Equal(expected, PiWebSearchCapabilities.ResolveModelReference(model));
    }
}
