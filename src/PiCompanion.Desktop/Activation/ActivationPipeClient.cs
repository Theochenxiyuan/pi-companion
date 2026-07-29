using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Activation;

internal static class ActivationPipeClient
{
    public static bool TrySend(ExplorerActivationRequest request, TimeSpan timeout)
    {
        var payload = ExplorerActivationCodec.Serialize(request);
        var timeoutMilliseconds = Math.Clamp((int)timeout.TotalMilliseconds, 1, 30_000);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ActivationPipeName.ForCurrentUser(),
                PipeDirection.Out,
                PipeOptions.None);
            pipe.Connect(timeoutMilliseconds);

            Span<byte> header = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            pipe.Write(header);
            pipe.Write(payload);
            pipe.Flush();
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
