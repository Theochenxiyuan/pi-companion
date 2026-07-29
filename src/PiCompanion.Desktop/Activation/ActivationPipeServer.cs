using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Activation;

internal sealed class ActivationPipeServer : IDisposable
{
    private readonly Action<ExplorerActivationRequest> _onActivation;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenTask;

    public ActivationPipeServer(
        Action<ExplorerActivationRequest> onActivation,
        Action<Exception>? onError = null)
    {
        _onActivation = onActivation;
        _onError = onError;
    }

    public void Start()
    {
        if (_listenTask is not null)
        {
            throw new InvalidOperationException("Explorer 激活管道已经启动。");
        }

        _listenTask = Task.Run(() => ListenAsync(_shutdown.Token));
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }

        _shutdown.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServer();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var request = await ReadRequestAsync(pipe, cancellationToken).ConfigureAwait(false);
                _onActivation(request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _onError?.Invoke(exception);
            }
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        ActivationPipeName.ForCurrentUser(),
        PipeDirection.In,
        4,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        4096,
        ExplorerActivationProtocol.MaximumPayloadBytes + sizeof(int));

    private static async Task<ExplorerActivationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ExplorerActivationProtocol.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Explorer 激活请求的帧长度无效。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return ExplorerActivationCodec.Deserialize(payload);
    }
}
