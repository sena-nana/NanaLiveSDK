using System.Net.WebSockets;
using System.Threading.Channels;

namespace Nanalive.Sdk;

/// <summary><see cref="ConnectAsync"/> 的选项。</summary>
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
}

/// <summary>基于 BCL <see cref="ClientWebSocket"/> 的连接：客户端 + 后台收发泵任务。</summary>
public sealed class NanaLiveConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly Channel<byte[]> _outbound;
    private readonly Task _receiveLoop;
    private readonly Task _sendLoop;

    public NanaLiveClient Client { get; }

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
        var webSocket = new ClientWebSocket();
        webSocket.Options.AddSubProtocol(NanaLiveApi.Subprotocol);
        await webSocket.ConnectAsync(
            new Uri($"ws://{options.Host}:{options.Port}/"), cancellationToken);

        var outbound = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        var client = new NanaLiveClient(
            payload => outbound.Writer.TryWrite(payload),
            options.Identity,
            options.Token,
            options.OnToken);

        var receiveLoop = Task.Run(
            () => ReceiveLoopAsync(webSocket, client, options.OnUnhandled, options.OnError));
        var sendLoop = Task.Run(() => SendLoopAsync(webSocket, outbound.Reader));
        return new NanaLiveConnection(webSocket, client, outbound, receiveLoop, sendLoop);
    }

    /// <summary>优雅关闭连接并等待泵任务退出。</summary>
    public async Task CloseAsync()
    {
        _outbound.Writer.TryComplete();
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(_receiveLoop, _sendLoop);
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
