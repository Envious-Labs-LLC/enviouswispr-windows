using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace EnviousWispr.Services.Lifecycle;

public sealed class SingleInstanceActivationChannel : IAsyncDisposable
{
    private const byte ActivationMessage = 0xA1;

    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceActivationChannel(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _pipeName = PipeName(key);
    }

    public event EventHandler? ActivationRequested;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The activation channel is already listening.");
        }

        _listener = ListenAsync(_lifetime.Token);
    }

    public static async Task<bool> RequestActivationAsync(
        string key,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, TimeSpan.FromSeconds(5));
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName(key),
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            await client.WriteAsync(
                new ReadOnlyMemory<byte>([ActivationMessage]),
                timeoutCancellation.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            OperationCanceledException or
            TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_listener is not null)
        {
            try
            {
                await _listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _listener = null;
        }

        _lifetime.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var message = new byte[1];
                var read = await server.ReadAsync(message, cancellationToken).ConfigureAwait(false);
                if (read == 1 && message[0] == ActivationMessage)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static string PipeName(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"EnviousWispr.Activation.{Convert.ToHexString(bytes.AsSpan(0, 12))}";
    }
}
