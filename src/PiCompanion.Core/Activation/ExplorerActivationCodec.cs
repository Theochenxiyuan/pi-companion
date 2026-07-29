using System.Text;
using System.Text.Json;

namespace PiCompanion.Core.Activation;

public static class ExplorerActivationCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(ExplorerActivationRequest request)
    {
        var normalized = ExplorerActivationValidator.Normalize(request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (payload.Length > ExplorerActivationProtocol.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Explorer 激活请求超过大小限制。");
        }

        return payload;
    }

    public static ExplorerActivationRequest Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > ExplorerActivationProtocol.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Explorer 激活请求大小无效。");
        }

        try
        {
            var json = StrictUtf8.GetString(payload);
            var request = JsonSerializer.Deserialize<ExplorerActivationRequest>(json, JsonOptions)
                ?? throw new InvalidDataException("Explorer 激活请求为空。");
            return ExplorerActivationValidator.Normalize(request);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Explorer 激活请求不是有效 JSON。", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Explorer 激活请求不是有效 UTF-8。", exception);
        }
    }
}
