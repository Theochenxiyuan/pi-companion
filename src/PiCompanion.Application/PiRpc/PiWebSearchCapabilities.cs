namespace PiCompanion.Application.PiRpc;

public enum PiWebSearchSupport
{
    None,
    Native,
}

public static class PiWebSearchCapabilities
{
    public static PiWebSearchSupport ResolveModelReference(string? modelReference)
    {
        if (string.IsNullOrWhiteSpace(modelReference))
        {
            return PiWebSearchSupport.None;
        }

        var separator = modelReference.IndexOf('/');
        if (separator <= 0)
        {
            return PiWebSearchSupport.None;
        }

        return modelReference[..separator] switch
        {
            "openai" or "google" or "anthropic" or "openai-codex" => PiWebSearchSupport.Native,
            _ => PiWebSearchSupport.None,
        };
    }
}
