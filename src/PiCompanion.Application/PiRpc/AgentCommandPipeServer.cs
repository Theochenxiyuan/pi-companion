using System.Buffers.Binary;
using System.IO.Pipes;

namespace PiCompanion.Application.PiRpc;

internal sealed class AgentCommandPipeServer : IDisposable
{
    public const int MaximumRequestBytes = 256 * 1024;
    public const int MaximumResponseBytes = 32 * 1024;

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> _handler;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenTask;

    public AgentCommandPipeServer(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> handler,
        Action<Exception>? onError = null,
        string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _onError = onError;
        PipeName = string.IsNullOrWhiteSpace(pipeName)
            ? $"PiCompanion.AgentCommands.v1.{Guid.NewGuid():N}"
            : pipeName;
    }

    public string PipeName { get; }

    public void Start()
    {
        if (_listenTask is not null)
        {
            throw new InvalidOperationException("Agent 命令管道已经启动。");
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
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException))
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
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var request = await ReadFrameAsync(
                    pipe,
                    MaximumRequestBytes,
                    requestTimeout.Token).ConfigureAwait(false);
                var response = await _handler(request, requestTimeout.Token).ConfigureAwait(false);
                if (response.Length is <= 0 or > MaximumResponseBytes)
                {
                    throw new InvalidDataException("Agent 命令响应长度无效。");
                }

                await WriteFrameAsync(pipe, response, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException exception)
            {
                _onError?.Invoke(new TimeoutException("Agent 命令请求处理超时。", exception));
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _onError?.Invoke(exception);
            }
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        PipeName,
        PipeDirection.InOut,
        2,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        MaximumResponseBytes + sizeof(int),
        MaximumRequestBytes + sizeof(int));

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("Agent 命令帧长度无效。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
