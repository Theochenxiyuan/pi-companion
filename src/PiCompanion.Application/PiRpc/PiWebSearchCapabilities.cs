namespace PiCompanion.Application.PiRpc;

public enum PiWebSearchIntegration
{
    None,
    CompanionTool,
    ProviderRequest,
}

public static class PiWebSearchCapabilities
{
    public static PiWebSearchIntegration ResolveModelReference(string? modelReference)
    {
        if (string.IsNullOrWhiteSpace(modelReference))
        {
            return PiWebSearchIntegration.None;
        }

        var separator = modelReference.IndexOf('/');
        if (separator <= 0)
        {
            return PiWebSearchIntegration.None;
        }

        var provider = modelReference[..separator];
        var model = modelReference[(separator + 1)..];
        if (provider == "xiaomi" && model is "mimo-v2.5" or "mimo-v2.5-pro")
        {
            return PiWebSearchIntegration.ProviderRequest;
        }

        return provider switch
        {
            "openai" or "google" or "anthropic" or "openai-codex" => PiWebSearchIntegration.CompanionTool,
            _ => PiWebSearchIntegration.None,
        };
    }
}
