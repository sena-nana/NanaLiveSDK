using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit;

namespace Nanalive.Sdk.Tests;

public sealed class SessionTests
{
    private static Identity TestIdentity => new(
        "dev.example.plugin", "Example", "Example", "0.1.0", new[] { "model.read" });

    [Fact]
    public async Task Session_Reconnects_AfterServerDrop()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.AnswerThenDropFirst);
        var statuses = new List<NanaLiveSessionStatus>();
        server.RunAsync();

        await using (var session = await NanaLiveSession.ConnectAsync(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
            OnStatus = statuses.Add,
        }))
        {
            var first = await session.RequestAsync("AvailableModelsRequest");
            Assert.Equal("m-1", FirstModelId(first));

            // 第一条连接在回答后被服务端强制断开；重连后再次查询应成功。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var reconnected = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var models = await session.RequestAsync("AvailableModelsRequest");
                    Assert.Equal("m-1", FirstModelId(models));
                    reconnected = true;
                    break;
                }
                catch (NanaLiveConnectionException)
                {
                    await Task.Delay(50);
                }
            }
            Assert.True(reconnected, "重连后未能再次完成请求");
        }

        Assert.True(statuses.Count(s => s == NanaLiveSessionStatus.Connected) >= 2);
        Assert.Contains(NanaLiveSessionStatus.Reconnecting, statuses);
        Assert.Equal(NanaLiveSessionStatus.Disconnected, statuses[^1]);
    }

    [Fact]
    public async Task Session_RequestTimeout_AndNotConnected()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.SilentOnModels);
        server.RunAsync();
        var session = await NanaLiveSession.ConnectAsync(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
            RequestTimeout = TimeSpan.FromMilliseconds(500),
        });

        await Assert.ThrowsAsync<NanaLiveRequestTimeoutException>(
            () => session.RequestAsync("AvailableModelsRequest"));
        await session.CloseAsync();

    }

    [Fact]
    public async Task Session_NotConnected_BeforeConnect_AndAfterClose()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.Answer);
        server.RunAsync();
        var session = new NanaLiveSession(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        });

        var exception = await Assert.ThrowsAsync<NanaLiveConnectionException>(
            () => session.RequestAsync("AvailableModelsRequest"));
        Assert.Equal("not_connected", exception.Message);

        await session.ConnectAsync();
        await session.CloseAsync();

        exception = await Assert.ThrowsAsync<NanaLiveConnectionException>(
            () => session.RequestAsync("AvailableModelsRequest"));
        Assert.Equal("not_connected", exception.Message);
        Assert.Equal(NanaLiveSessionStatus.Disconnected, session.Status);

    }

    [Fact]
    public async Task Session_PendingRequests_FailOnDrop()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.DropWithoutAnswer);
        server.RunAsync();
        var session = await NanaLiveSession.ConnectAsync(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        });

        var exception = await Assert.ThrowsAsync<NanaLiveConnectionException>(
            () => session.RequestAsync("AvailableModelsRequest"));
        Assert.Equal("connection_lost", exception.Message);

        await session.CloseAsync();
    }

    [Fact]
    public async Task Session_SecondConnect_ResetsCleanly()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.Answer);
        server.RunAsync();
        var session = await NanaLiveSession.ConnectAsync(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        });
        var first = await session.RequestAsync("AvailableModelsRequest");
        Assert.Equal("m-1", FirstModelId(first));

        // 第二次 ConnectAsync 必须能返回（先关旧连接再建新连接），会话仍可用。
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.ConnectAsync(timeoutCts.Token);
        Assert.True(session.IsConnected);
        var models = await session.RequestAsync("AvailableModelsRequest");
        Assert.Equal("m-1", FirstModelId(models));
        await session.CloseAsync();
    }

    [Fact]
    public async Task Session_ConnectTimeout_WhenHandshakeBlackHoles()
    {
        // 接受 TCP 连接但从不回应握手的"黑洞"服务端。
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await probe.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        await Task.Delay(Timeout.Infinite);
                    }
                });
            }
        });

        var session = new NanaLiveSession(new SessionOptions
        {
            Port = port,
            Identity = TestIdentity,
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            Reconnect = false,
            RetryDelay = TimeSpan.FromMilliseconds(20),
        });
        var exception = await Assert.ThrowsAsync<NanaLiveConnectionException>(
            () => session.ConnectAsync());
        Assert.Equal("connect_timeout", exception.Message);
    }

    [Fact]
    public async Task Session_CloseDuringConnect_DoesNotLeaveZombieSession()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.SlowAuth);
        server.RunAsync();
        var session = new NanaLiveSession(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            RetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        });

        var connectTask = session.ConnectAsync();
        await Task.Delay(100); // 建链进行中、鉴权未完成
        await session.CloseAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => connectTask);
        Assert.Equal(NanaLiveSessionStatus.Disconnected, session.Status);

        // 等慢半拍的鉴权回复到达，会话也不得复活。
        await Task.Delay(700);
        Assert.False(session.IsConnected);
        Assert.Equal(NanaLiveSessionStatus.Disconnected, session.Status);
        await session.CloseAsync(); // 幂等
    }

    [Fact]
    public async Task Session_RequestCancellation_IsNotReportedAsTimeout()
    {
        using var server = new MockNanaLiveServer(ModelsBehavior.SilentOnModels);
        server.RunAsync();
        var session = await NanaLiveSession.ConnectAsync(new SessionOptions
        {
            Port = server.Port,
            Identity = TestIdentity,
            RequestTimeout = TimeSpan.FromSeconds(30),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // 取消必须抛 OperationCanceledException（含其子类），而不是被误报成请求超时。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RequestAsync("AvailableModelsRequest", null, cts.Token));
        await session.CloseAsync();
    }

    private static string FirstModelId(object? response) =>
        response!.GetField("data")!.GetField("models")!.TryList()![0]
            .GetField("modelID")!.TryString()!;

    /// <summary>服务端对 <c>AvailableModelsRequest</c> 的处理方式。</summary>
    private enum ModelsBehavior
    {
        /// <summary>正常回答。</summary>
        Answer,

        /// <summary>先回答再强制断开第一条连接，模拟服务器崩溃。</summary>
        AnswerThenDropFirst,

        /// <summary>不回答直接断开，模拟挂起中的请求在断线时失败。</summary>
        DropWithoutAnswer,

        /// <summary>不回答也不断开，用于请求超时测试。</summary>
        SilentOnModels,

        /// <summary>AuthenticationTokenRequest 先拖 500ms 再回答（留出 close 窗口）。</summary>
        SlowAuth,
    }

    /// <summary>本地 mock 服务端：循环接受多条连接（会话层重连会用）。</summary>
    private sealed class MockNanaLiveServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ModelsBehavior _behavior;

        public int Port { get; }

        public MockNanaLiveServer(ModelsBehavior behavior)
        {
            _behavior = behavior;
            // 先探测一个空闲端口，再交给 HttpListener。
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }

        public Task RunAsync() => Task.Run(async () =>
        {
            var connectionIndex = 0;
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener 已关闭
                }

                var index = connectionIndex++;
                _ = Task.Run(() => HandleAsync(context, index));
            }
        });

        private async Task HandleAsync(HttpListenerContext context, int index)
        {
            var requested = context.Request.Headers["Sec-WebSocket-Protocol"];
            var webSocket = (await context.AcceptWebSocketAsync(requested)).WebSocket;

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

                var request = Mp.Deserialize(message.ToArray())!;
                var requestId = request.GetField("requestID")!.TryString()!;
                var messageType = request.GetField("messageType")!.TryString()!;

                if (_behavior == ModelsBehavior.SlowAuth
                    && messageType == "AuthenticationTokenRequest")
                {
                    // 慢鉴权：拖过 close() 的窗口后再回答。
                    await Task.Delay(500);
                    var delayed = Route(requestId, messageType);
                    if (delayed is not null)
                    {
                        await webSocket.SendAsync(
                            Mp.Serialize(delayed),
                            WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    continue;
                }

                var response = Route(requestId, messageType);
                if (response is null)
                {
                    continue;
                }

                var payload = Mp.Serialize(response);
                if (messageType == "AvailableModelsRequest")
                {
                    switch (_behavior)
                    {
                        case ModelsBehavior.AnswerThenDropFirst when index == 0:
                            await webSocket.SendAsync(
                                payload, WebSocketMessageType.Binary, true, CancellationToken.None);
                            webSocket.Abort(); // 模拟服务器崩溃
                            return;
                        case ModelsBehavior.DropWithoutAnswer:
                            webSocket.Abort();
                            return;
                        case ModelsBehavior.SilentOnModels:
                            continue;
                    }
                }

                await webSocket.SendAsync(
                    payload, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }

        private static object? Route(string requestId, string messageType) => messageType switch
        {
            "AuthenticationTokenRequest" => TestUtil.Envelope(
                requestId,
                "AuthenticationTokenResponse",
                Mp.Map(("authenticationToken", Mp.Str("issued-token")))),
            "AuthenticationRequest" => TestUtil.Envelope(
                requestId, "AuthenticationResponse", Mp.Map()),
            "AvailableModelsRequest" => TestUtil.Envelope(
                requestId,
                "AvailableModelsResponse",
                Mp.Map(("models", Mp.Array(Mp.Map(("modelID", Mp.Str("m-1"))))))),
            _ => null,
        };

        public void Dispose() => _listener.Close();
    }
}
