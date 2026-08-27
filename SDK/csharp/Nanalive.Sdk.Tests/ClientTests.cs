using Nanalive.Sdk;
using Xunit;

namespace Nanalive.Sdk.Tests;

public class ClientTests
{
    private sealed class Mock
    {
        public List<byte[]> Sent { get; } = new();
        public List<string> IssuedTokens { get; } = new();
        public NanaLiveClient Client { get; }

        public Mock(Identity? identity = null, string? token = null)
        {
            Client = new NanaLiveClient(
                Sent.Add,
                identity,
                token,
                tokenText => IssuedTokens.Add(tokenText));
        }
    }

    [Fact]
    public async Task Envelope_HasFixedFieldsAndIncreasingRequestIds()
    {
        var mock = new Mock(identity: new Identity("x", "n", "d", "0.1.0", Array.Empty<string>()));
        var firstTask = mock.Client.RequestAsync("AvailableModelsRequest");
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var first = TestUtil.Decode(mock.Sent[0]);

        Assert.Equal(NanaLiveApi.ApiName, first.GetField("apiName")!.TryString());
        Assert.Equal(NanaLiveApi.ApiVersion, first.GetField("apiVersion")!.TryString());
        Assert.Equal("AvailableModelsRequest", first.GetField("messageType")!.TryString());
        Assert.Equal("nanalive-1", first.GetField("requestID")!.TryString());
        Assert.True(first.GetField("data")!.IsMap());
        _ = firstTask;

        var secondTask = mock.Client.RequestAsync("MotionListRequest");
        await TestUtil.WaitForAsync(mock.Sent, 2);
        var second = TestUtil.Decode(mock.Sent[1]);
        Assert.Equal("nanalive-2", second.GetField("requestID")!.TryString());
        _ = secondTask;
    }

    [Fact]
    public async Task Response_IsPairedAndUnhandledPushIsPassedThrough()
    {
        var mock = new Mock();
        var task = mock.Client.RequestAsync("HotkeyListRequest");
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var sent = TestUtil.Decode(mock.Sent[0]);
        var requestId = sent.GetField("requestID")!.TryString()!;

        // 无匹配等待者的推送先到达，应原样透传。
        var push = TestUtil.Envelope("nanalive-other", "SomePush", Mp.Map(("n", Mp.Num(1))));
        Assert.NotNull(mock.Client.ReceiveValue(push));

        // 配对响应。
        var response = TestUtil.Envelope(
            requestId, "HotkeyListResponse", Mp.Map(("hotkeys", Mp.Array())));
        Assert.Null(mock.Client.ReceiveValue(response));

        var result = await task;
        Assert.True(result.GetField("data")!.GetField("hotkeys")!.TryList() is { Count: 0 });
    }

    [Fact]
    public async Task ApiErrorResponse_RejectsWithCode()
    {
        var mock = new Mock();
        var task = mock.Client.RequestAsync("MotionTriggerRequest", Mp.Map(("motionID", Mp.Str("m1"))));
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var sent = TestUtil.Decode(mock.Sent[0]);

        var error = TestUtil.Envelope(
            sent.GetField("requestID")!.TryString()!,
            "APIError",
            Mp.Map(("message", Mp.Str("motion not found")), ("errorCode", Mp.Str("motion_not_found"))));
        mock.Client.ReceiveValue(error);

        var exception = await Assert.ThrowsAsync<NanaLiveApiException>(() => task);
        Assert.Equal("motion not found", exception.Message);
        Assert.Equal("motion_not_found", exception.Code!.TryString());
    }

    [Fact]
    public async Task Authenticate_WithValidTokenOnlyVerifiesOnce()
    {
        var mock = new Mock(token: "saved-token");
        var task = mock.Client.AuthenticateAsync();
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var sent = TestUtil.Decode(mock.Sent[0]);

        Assert.Equal("AuthenticationRequest", sent.GetField("messageType")!.TryString());
        Assert.Equal(
            "saved-token",
            sent.GetField("data")!.GetField("authenticationToken")!.TryString());

        mock.Client.ReceiveValue(TestUtil.Envelope(
            sent.GetField("requestID")!.TryString()!, "AuthenticationResponse", Mp.Map()));
        await task;
        Assert.Single(mock.Sent);
        Assert.Empty(mock.IssuedTokens);
    }

    [Fact]
    public async Task Authenticate_FallsBackWhenSavedTokenIsRejected()
    {
        var identity = new Identity(
            "dev.example.plugin", "Example", "Example", "0.1.0", new[] { "model.read" });
        var mock = new Mock(identity: identity, token: "stale-token");
        var task = mock.Client.AuthenticateAsync();

        // 第一步：旧 token 验证被拒。
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var first = TestUtil.Decode(mock.Sent[0]);
        Assert.Equal("AuthenticationRequest", first.GetField("messageType")!.TryString());
        mock.Client.ReceiveValue(TestUtil.Envelope(
            first.GetField("requestID")!.TryString()!,
            "APIError",
            Mp.Map(("message", Mp.Str("invalid token")))));

        // 第二步：降级申请新 token。
        await TestUtil.WaitForAsync(mock.Sent, 2);
        var second = TestUtil.Decode(mock.Sent[1]);
        Assert.Equal("AuthenticationTokenRequest", second.GetField("messageType")!.TryString());
        Assert.Equal("dev.example.plugin", second.GetField("data")!.GetField("pluginID")!.TryString());
        mock.Client.ReceiveValue(TestUtil.Envelope(
            second.GetField("requestID")!.TryString()!,
            "AuthenticationTokenResponse",
            Mp.Map(("authenticationToken", Mp.Str("fresh-token")))));

        // 第三步：用新 token 验证。
        await TestUtil.WaitForAsync(mock.Sent, 3);
        var third = TestUtil.Decode(mock.Sent[2]);
        Assert.Equal("AuthenticationRequest", third.GetField("messageType")!.TryString());
        Assert.Equal(
            "fresh-token",
            third.GetField("data")!.GetField("authenticationToken")!.TryString());
        mock.Client.ReceiveValue(TestUtil.Envelope(
            third.GetField("requestID")!.TryString()!, "AuthenticationResponse", Mp.Map()));

        await task;
        Assert.Equal(new[] { "fresh-token" }, mock.IssuedTokens);
    }

    [Fact]
    public async Task Authenticate_FailsWhenNoTokenIsIssued()
    {
        var mock = new Mock();
        var task = mock.Client.AuthenticateAsync();
        await TestUtil.WaitForAsync(mock.Sent, 1);
        var sent = TestUtil.Decode(mock.Sent[0]);
        mock.Client.ReceiveValue(TestUtil.Envelope(
            sent.GetField("requestID")!.TryString()!,
            "AuthenticationTokenResponse",
            Mp.Map()));

        await Assert.ThrowsAsync<AuthenticationTokenMissingException>(() => task);
    }
}
