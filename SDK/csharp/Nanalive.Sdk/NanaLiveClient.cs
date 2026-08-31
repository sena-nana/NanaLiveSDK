namespace Nanalive.Sdk;

/// <summary>NanaLive 插件 API 客户端。</summary>
/// <remarks>
/// 与传输解耦：构造时注入同步的 <c>send</c> 回调负责把编码后的字节写出去，
/// 收到字节后调用 <see cref="Receive"/> 喂回客户端即可。
/// </remarks>
public sealed class NanaLiveClient
{
    private readonly Action<byte[]> _send;
    private readonly Identity? _identity;
    private readonly Action<string>? _onToken;
    private readonly Dictionary<string, TaskCompletionSource<object?>> _waiters = new();
    private readonly object _gate = new();
    private string? _token;
    private int _sequence;

    public NanaLiveClient(
        Action<byte[]> send,
        Identity? identity = null,
        string? token = null,
        Action<string>? onToken = null)
    {
        _send = send ?? throw new ArgumentException("send is required", nameof(send));
        _identity = identity;
        _token = token;
        _onToken = onToken;
    }

    /// <summary>发送一条请求并等待配对的响应。</summary>
    public async Task<object?> RequestAsync(
        string messageType, object? data = null, CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var requestId = $"nanalive-{sequence}";
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters[requestId] = completion;
        }

        var envelope = Mp.Map(
            ("apiName", Mp.Str(NanaLiveApi.ApiName)),
            ("apiVersion", Mp.Str(NanaLiveApi.ApiVersion)),
            ("requestID", Mp.Str(requestId)),
            ("messageType", Mp.Str(messageType)),
            ("data", data ?? Mp.Map()));
        try
        {
            _send(Mp.Serialize(envelope));
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _waiters.Remove(requestId);
            }
            completion.TrySetException(error);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.CanBeCanceled)
        {
            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 放弃等待后，迟到的失败不能变成 UnobservedTaskException。
                _ = completion.Task.ContinueWith(
                    task => _ = task.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
        }
        return await completion.Task;
    }

    /// <summary>把一段收到的字节喂回客户端。</summary>
    /// <returns>
    /// 返回 null 表示响应已配对给等待中的请求；返回非 null 表示没有匹配的
    /// 等待者（服务器主动推送），原样透传给调用方。
    /// </returns>
    public object? Receive(byte[] payload)
    {
        var response = Mp.Deserialize(payload);
        return ReceiveValue(response);
    }

    /// <summary>已解码响应的配对逻辑，见 <see cref="Receive"/>。</summary>
    public object? ReceiveValue(object? response)
    {
        var requestId = response.GetField("requestID")?.TryString();
        TaskCompletionSource<object?>? completion = null;
        if (requestId is not null)
        {
            lock (_gate)
            {
                if (_waiters.Remove(requestId, out var found))
                {
                    completion = found;
                }
            }
        }

        if (completion is null)
        {
            return response;
        }

        if (response.GetField("messageType")?.TryString() == "APIError")
        {
            var data = response.GetField("data");
            var message = data.GetField("message")?.TryString() ?? "api_error";
            completion.TrySetException(new NanaLiveApiException(message, data.GetField("errorCode")));
        }
        else
        {
            completion.TrySetResult(response);
        }

        return null;
    }

    /// <summary>让所有等待中的请求立即失败（连接断开时由会话层调用）。</summary>
    /// <returns>清掉的等待者数量。</returns>
    public int FailPending(Exception error)
    {
        TaskCompletionSource<object?>[] pending;
        lock (_gate)
        {
            pending = _waiters.Values.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in pending)
        {
            waiter.TrySetException(error);
        }
        return pending.Length;
    }

    /// <summary>两段式鉴权：已有 token 被服务端拒绝时降级为申请新 token。</summary>
    /// <remarks>
    /// 只有服务端明确返回 APIError（而非网络闪断、超时等传输层故障）才会
    /// 轮换 token；传输层异常原样传播。
    /// </remarks>
    public async Task<object?> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        string? saved;
        lock (_gate)
        {
            saved = _token;
        }

        if (saved is not null)
        {
            try
            {
                return await RequestAsync(
                    "AuthenticationRequest", Mp.Map(("authenticationToken", Mp.Str(saved))),
                    cancellationToken);
            }
            catch (NanaLiveApiException)
            {
                lock (_gate)
                {
                    _token = null;
                }
            }
        }

        var issued = await RequestAsync(
            "AuthenticationTokenRequest", _identity?.ToMessagePack() ?? Mp.Nil, cancellationToken);
        var token = issued.GetField("data").GetField("authenticationToken")?.TryString();
        if (string.IsNullOrEmpty(token))
        {
            throw new AuthenticationTokenMissingException();
        }

        lock (_gate)
        {
            _token = token;
        }

        _onToken?.Invoke(token!);
        return await RequestAsync(
            "AuthenticationRequest", Mp.Map(("authenticationToken", Mp.Str(token!))),
            cancellationToken);
    }

    /// <summary><c>AvailableModelsRequest</c>。</summary>
    public Task<object?> ListModelsAsync() => RequestAsync("AvailableModelsRequest");

    /// <summary><c>MotionListRequest</c>。</summary>
    public Task<object?> ListMotionsAsync() => RequestAsync("MotionListRequest");

    /// <summary><c>ExpressionListRequest</c>。</summary>
    public Task<object?> ListExpressionsAsync() => RequestAsync("ExpressionListRequest");

    /// <summary><c>HotkeyListRequest</c>。</summary>
    public Task<object?> ListHotkeysAsync() => RequestAsync("HotkeyListRequest");

    /// <summary><c>ParameterListRequest</c>。</summary>
    public Task<object?> ListParametersAsync() => RequestAsync("ParameterListRequest");
}
