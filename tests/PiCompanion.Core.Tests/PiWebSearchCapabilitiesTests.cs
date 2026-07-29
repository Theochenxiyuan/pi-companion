using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class PiWebSearchCapabilitiesTests
{
    [Theory]
    [InlineData("openai/gpt-5.4", PiWebSearchSupport.Native)]
    [InlineData("google/gemini-2.5-pro", PiWebSearchSupport.Native)]
    [InlineData("anthropic/claude-sonnet-4", PiWebSearchSupport.Native)]
    [InlineData("openai-codex/gpt-5.6", PiWebSearchSupport.Native)]
    [InlineData("company-proxy/gpt-5.4", PiWebSearchSupport.None)]
    [InlineData("Pi 默认模型", PiWebSearchSupport.None)]
    public void ResolveModelReference_RestrictsSearchToApprovedOfficialProviders(
        string model,
        PiWebSearchSupport expected)
    {
        Assert.Equal(expected, PiWebSearchCapabilities.ResolveModelReference(model));
    }
}
