using System.Net.WebSockets;
using System.Threading.Channels;

namespace Nanalive.Sdk;

/// <summary><see cref="NanaLiveConnection.ConnectAsync"/> 的选项。</summary>
public sealed class ConnectOptions
{
    /// <summary>默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>默认 <see cref="NanaLiveApi.DefaultPort"/>（8312）。</summary>
    public int Port { get; set; } = NanaLiveApi.DefaultPort;

    public Identity? Identity { get; set; }

    public string? Token { get; set; }

    /// <summary>首次签发的 token，用于调用方持久化。</summary>
    public Action<string>? OnToken { get; set; }

    /// <summary>未配对请求的响应（服务器主动推送）。</summary>
    public Action<object?>? OnUnhandled { get; set; }

    /// <summary>泵任务中的协议/连接错误。</summary>
    public Action<string>? OnError { get; set; }

    /// <summary>
    /// WebSocket 协议级心跳间隔（<see cref="ClientWebSocket.Options.KeepAliveInterval"/>）。
    /// <c>null</c> 时沿用 BCL 默认值（30 秒）；会话层会把它设为心跳间隔。
    /// BCL 未暴露独立的 pong 超时：运行时按 KeepAliveInterval 的一半判死，
    /// 即 10 秒间隔下约 5 秒无 pong 断开（与其他语言默认心跳超时一致）。
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }
}

/// <summary>基于 BCL <see cref="ClientWebSocket"/> 的连接：客户端 + 后台收发泵任务。</summary>
public sealed class NanaLiveConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly Channel<byte[]> _outbound;
    private readonly Task _receiveLoop;
    private readonly Task _sendLoop;

    public NanaLiveClient Client { get; }

    /// <summary>泵任务全部退出后完成（正常关闭与断线都会触发）。</summary>
    public Task Completion { get; }

    /// <summary>出站通道；会话层把它绑定到共享客户端的 send 回调。</summary>
    internal ChannelWriter<byte[]> Outbound => _outbound.Writer;

    private NanaLiveConnection(
        ClientWebSocket webSocket,
        NanaLiveClient client,
        Channel<byte[]> outbound,
        Task receiveLoop,
        Task sendLoop)
    {
        _webSocket = webSocket;
        Client = client;
        _outbound = outbound;
        _receiveLoop = receiveLoop;
        _sendLoop = sendLoop;
        Completion = Task.WhenAll(receiveLoop, sendLoop);
    }

    /// <summary>连接 NanaLive 控制 API。</summary>
    /// <remarks>
    /// 泵任务把入站 MessagePack 帧喂给 <see cref="NanaLiveClient.Receive"/>，
    /// 客户端 <c>send</c> 的字节经出站通道写回 WebSocket。
    /// </remarks>
    public static async Task<NanaLiveConnection> ConnectAsync(
        ConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ConnectOptions();
        var outbound = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        var client = new NanaLiveClient(
            payload => outbound.Writer.TryWrite(payload),
            options.Identity,
            options.Token,
            options.OnToken);
        return await ConnectAsync(options, client, outbound, cancellationToken);
    }

    /// <summary>
    /// 同 <see cref="ConnectAsync(ConnectOptions?, CancellationToken)"/>，
    /// 但复用调用方提供的客户端（会话层跨重连共享 token 与等待队列）。
    /// </summary>
    public static Task<NanaLiveConnection> ConnectAsync(
        ConnectOptions options, NanaLiveClient client, CancellationToken cancellationToken = default)
    {
        var outbound = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        return ConnectAsync(options, client, outbound, cancellationToken);
    }

    private static async Task<NanaLiveConnection> ConnectAsync(
        ConnectOptions options,
        NanaLiveClient client,
        Channel<byte[]> outbound,
        CancellationToken cancellationToken)
    {
        var webSocket = new ClientWebSocket();
        if (options.KeepAliveInterval is { } keepAlive)
        {
            webSocket.Options.KeepAliveInterval = keepAlive;
        }
        webSocket.Options.AddSubProtocol(NanaLiveApi.Subprotocol);
        await webSocket.ConnectAsync(
            new Uri($"ws://{options.Host}:{options.Port}/"), cancellationToken);

        var receiveLoop = Task.Run(
            () => ReceiveLoopAsync(webSocket, client, outbound.Writer, options.OnUnhandled, options.OnError));
        var sendLoop = Task.Run(() => SendLoopAsync(webSocket, outbound.Reader));
        return new NanaLiveConnection(webSocket, client, outbound, receiveLoop, sendLoop);
    }

    /// <summary>优雅关闭连接并等待泵任务退出。</summary>
    public async Task CloseAsync()
    {
        _outbound.Writer.TryComplete();
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception)
            {
                // 网络已死时优雅关闭必然失败：直接中止，让泵任务退出。
                _webSocket.Abort();
            }
        }

        try
        {
            await Completion;
        }
        catch (Exception)
        {
            // 关闭阶段的异常不影响调用方。
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private static async Task ReceiveLoopAsync(
        ClientWebSocket webSocket,
        NanaLiveClient client,
        ChannelWriter<byte[]> outbound,
        Action<object?>? onUnhandled,
        Action<string>? onError)
    {
        try
        {
            await ReceiveMessagesAsync(webSocket, client, onUnhandled, onError);
        }
        finally
        {
            // 接收泵退出即连接结束；关闭出站通道让发送泵也随之退出，
            // 否则意外断线时 Completion（WhenAll）永远等不到发送泵。
            outbound.TryComplete();
        }
    }

    private static async Task ReceiveMessagesAsync(
        ClientWebSocket webSocket,
        NanaLiveClient client,
        Action<object?>? onUnhandled,
        Action<string>? onError)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            message.SetLength(0);
            do
            {
                result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                continue;
            }

            try
            {
                var unhandled = client.Receive(message.ToArray());
                if (unhandled is not null)
                {
                    onUnhandled?.Invoke(unhandled);
                }
            }
            catch (Exception exception)
            {
                onError?.Invoke(exception.Message);
            }
        }
    }

    private static async Task SendLoopAsync(
        ClientWebSocket webSocket, ChannelReader<byte[]> reader)
    {
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var payload))
            {
                await webSocket.SendAsync(
                    payload, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
    }
}
