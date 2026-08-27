using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Nanalive.Sdk;
using Xunit;

namespace Nanalive.Sdk.Tests;

public sealed class ConnectionTests : IDisposable
{
    private readonly MockNanaLiveServer _server = new();

    [Fact]
    public async Task Connect_AuthenticateAndListModels()
    {
        var issued = new List<string>();
        var serverTask = _server.RunOnceAsync();

        await using (var connection = await NanaLiveConnection.ConnectAsync(new ConnectOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            Identity = new Identity(
                "dev.example.plugin", "Example", "Example", "0.1.0", new[] { "model.read" }),
            OnToken = issued.Add,
        }))
        {
            await connection.Client.AuthenticateAsync();
            var models = await connection.Client.ListModelsAsync();

            var first = models.GetField("data")!.GetField("models")!.TryList()![0];
            Assert.Equal("m-1", first.GetField("modelID")!.TryString());
            Assert.Equal(new[] { "issued-token" }, issued);
            Assert.Equal(NanaLiveApi.Subprotocol, _server.SeenSubprotocol);
        }

        // 先让连接关闭（块退出时 DisposeAsync），服务端循环才能退出。
        await serverTask;
    }

    public void Dispose() => _server.Dispose();

    /// <summary>本地 mock 服务端：回答鉴权与模型目录请求，并记录请求到的子协议。</summary>
    private sealed class MockNanaLiveServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public int Port { get; }
        public string? SeenSubprotocol { get; private set; }

        public MockNanaLiveServer()
        {
            // 先探测一个空闲端口，再交给 HttpListener。
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }

        public Task RunOnceAsync() => Task.Run(async () =>
        {
            var context = await _listener.GetContextAsync();
            var requested = context.Request.Headers["Sec-WebSocket-Protocol"];
            SeenSubprotocol = requested;
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
                var response = Route(request);
                if (response is not null)
                {
                    await webSocket.SendAsync(
                        Mp.Serialize(response),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None);
                }
            }
        });

        private static object? Route(object request)
        {
            var requestId = request.GetField("requestID")!.TryString()!;
            return request.GetField("messageType")!.TryString() switch
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
        }

        public void Dispose() => _listener.Close();
    }
}
