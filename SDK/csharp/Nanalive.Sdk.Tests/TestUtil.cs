using Nanalive.Sdk;
using Xunit;

namespace Nanalive.Sdk.Tests;

public static class TestUtil
{
    public static object Envelope(string requestId, string messageType, object? data) =>
        Mp.Map(
            ("apiName", Mp.Str(NanaLiveApi.ApiName)),
            ("apiVersion", Mp.Str(NanaLiveApi.ApiVersion)),
            ("requestID", Mp.Str(requestId)),
            ("messageType", Mp.Str(messageType)),
            ("data", data));

    public static object? Decode(byte[] payload) => Mp.Deserialize(payload);

    /// <summary>轮询等待客户端发出至少 n 条消息（避免竞争）。</summary>
    public static async Task WaitForAsync(List<byte[]> sent, int count)
    {
        for (var i = 0; i < 5000 && sent.Count < count; i++)
        {
            await Task.Delay(1);
        }

        Assert.True(sent.Count >= count, $"expected {count} messages, got {sent.Count}");
    }
}
