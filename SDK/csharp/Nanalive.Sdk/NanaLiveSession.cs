using System.Net.WebSockets;

namespace Nanalive.Sdk;

/// <summary>会话连接状态，经 <see cref="SessionOptions.OnStatus"/> 回调上报。</summary>
public enum NanaLiveSessionStatus
{
    /// <summary>正在建立首个连接。</summary>
    Connecting,

    /// <summary>已连接且完成鉴权。</summary>
    Connected,

    /// <summary>连接断开后正在重连。</summary>
    Reconnecting,

    /// <summary>已关闭，或重试耗尽后放弃。</summary>
    Disconnected,
}

/// <summary><see cref="NanaLiveSession"/> 的选项，语义与其他语言绑定的 session 一致。</summary>
public sealed class SessionOptions
{
    /// <summary>默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>默认 <see cref="NanaLiveApi.DefaultPort"/>（8312）。</summary>
    public int Port { get; set; } = NanaLiveApi.DefaultPort;

    public Identity? Identity { get; set; }

    /// <summary>初始 token；重连时由客户端内部复用最新 token。</summary>
    public string? Token { get; set; }

    /// <summary>首次签发的 token，用于调用方持久化。</summary>
    public Action<string>? OnToken { get; set; }

    /// <summary>未配对请求的响应（服务器主动推送）。</summary>
    public Action<object?>? OnUnhandled { get; set; }

    /// <summary>泵任务中的协议/连接错误。</summary>
    public Action<string>? OnError { get; set; }

    /// <summary>连接状态变化回调。</summary>
    public Action<NanaLiveSessionStatus>? OnStatus { get; set; }

    /// <summary>断线后是否自动重连，默认 <c>true</c>。</summary>
    public bool Reconnect { get; set; } = true;

    /// <summary>重试上限；<c>null</c> 表示无限重试。</summary>
    public int? MaxRetries { get; set; }

    /// <summary>首次重试延迟，默认 500ms，之后指数翻倍。</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>重试延迟上限，默认 8 秒。</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 心跳间隔，默认 10 秒；映射为 <see cref="ClientWebSocket.Options.KeepAliveInterval"/>，
    /// 空闲超过该时长即发送协议级 ping，pong 超时由 .NET 运行时内部处理。
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>单请求超时，默认 30 秒；<c>null</c> 表示不限制。</summary>
    public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>NanaLive 插件 API 的弹性会话：自动重连、心跳保活与请求超时。</summary>
/// <remarks>
/// 完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活；
/// 断线后挂起中的请求立即失败，并按指数退避（带抖动）自动重连与重新鉴权。
/// </remarks>
public sealed class NanaLiveSession : IAsyncDisposable
{
    private readonly SessionOptions _options;
    private readonly NanaLiveClient _client;
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<bool>> _connectWaiters = new();
    private NanaLiveConnection? _connection;
    private Func<byte[], bool>? _outbound;
    private NanaLiveSessionStatus _status = NanaLiveSessionStatus.Disconnected;
    private bool _closed;
    private int _attempt;
    private Task? _supervisor;

    /// <summary>创建会话；需要连接时调用 <see cref="ConnectAsync"/>。</summary>
    public NanaLiveSession(SessionOptions? options)
    {
        _options = options ?? new SessionOptions();
        _client = new NanaLiveClient(
            payload =>
            {
                Func<byte[], bool>? outbound;
                lock (_gate)
                {
                    outbound = _outbound;
                }
                if (outbound is null || !outbound(payload))
                {
                    throw new NanaLiveConnectionException("not_connected");
                }
            },
            _options.Identity,
            _options.Token,
            _options.OnToken);
    }

    /// <summary>创建会话并完成首次连接（含重试）与鉴权。</summary>
    public static async Task<NanaLiveSession> ConnectAsync(
        SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var session = new NanaLiveSession(options);
        await session.ConnectAsync(cancellationToken);
        return session;
    }

    /// <summary>底层协议客户端（token 在多次重连之间保持复用）。</summary>
    public NanaLiveClient Client => _client;

    public NanaLiveSessionStatus Status { get { lock (_gate) { return _status; } } }

    public bool IsConnected =>
        Status == NanaLiveSessionStatus.Connected && _connection is not null;

    /// <summary>建立会话（含重试），首个连接完成鉴权后返回。</summary>
    /// <remarks>
    /// 之后的断线由后台任务自动重连；重复调用是幂等的。重试耗尽（或
    /// <see cref="SessionOptions.Reconnect"/> 为 <c>false</c> 且连不上）时抛出最后的错误。
    /// </remarks>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task waiter;
        lock (_gate)
        {
            _closed = false;
            if (_supervisor is null || _supervisor.IsCompleted)
            {
                _attempt = 0;
                _supervisor = Task.Run(RunAsync);
            }
            if (_status == NanaLiveSessionStatus.Connected)
            {
                return;
            }
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectWaiters.Add(completion);
            waiter = completion.Task;
        }

        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _connectWaiters.RemoveAll(w => w.Task == waiter);
            }
        }))
        {
            await waiter.WaitAsync(cancellationToken);
        }
    }

    /// <summary>发送一条请求并等待配对的响应；断线时立刻失败。</summary>
    /// <exception cref="NanaLiveConnectionException">会话未连接。</exception>
    /// <exception cref="NanaLiveRequestTimeoutException">超过 <see cref="SessionOptions.RequestTimeout"/>。</exception>
    public async Task<object?> RequestAsync(
        string messageType, object? data = null, CancellationToken cancellationToken = default)
    {
        Func<byte[], bool>? outbound;
        lock (_gate)
        {
            outbound = _outbound;
        }
        if (outbound is null)
        {
            throw new NanaLiveConnectionException("not_connected");
        }

        var pending = _client.RequestAsync(messageType, data);
        if (_options.RequestTimeout is { } timeout)
        {
            var completed = await Task.WhenAny(pending, Task.Delay(timeout, cancellationToken));
            if (completed != pending)
            {
                _ = pending.ContinueWith(
                    task => _ = task.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
                throw new NanaLiveRequestTimeoutException();
            }
        }
        return await pending;
    }

    /// <summary>停止重连并关闭底层连接；挂起中的请求立即失败。</summary>
    public async Task CloseAsync()
    {
        Task? supervisor;
        lock (_gate)
        {
            _closed = true;
            FailConnectWaiters(new NanaLiveConnectionException("closed"));
            supervisor = _supervisor;
        }

        NanaLiveConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            _outbound = null;
        }
        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync();
            }
            catch (Exception)
            {
                // 关闭阶段的异常不影响调用方。
            }
        }
        if (supervisor is not null)
        {
            try
            {
                await supervisor;
            }
            catch (Exception)
            {
                // 监督任务的异常已在重连循环里处理。
            }
        }
        _client.FailPending(new NanaLiveConnectionException("connection_lost"));
        SetStatus(NanaLiveSessionStatus.Disconnected);
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>监督循环：连接 → 鉴权 → 等待断开 → 指数退避重连。</summary>
    private async Task RunAsync()
    {
        Exception? failure = null;
        while (true)
        {
            bool reconnecting;
            lock (_gate)
            {
                if (_closed)
                {
                    break;
                }
                reconnecting = _attempt > 0;
            }
            SetStatus(reconnecting
                ? NanaLiveSessionStatus.Reconnecting
                : NanaLiveSessionStatus.Connecting);

            NanaLiveConnection? connection = null;
            try
            {
                var connectOptions = new ConnectOptions
                {
                    Host = _options.Host,
                    Port = _options.Port,
                    KeepAliveInterval = _options.HeartbeatInterval,
                    OnUnhandled = _options.OnUnhandled,
                    OnError = _options.OnError,
                };
                connection = await NanaLiveConnection.ConnectAsync(connectOptions, _client);
                lock (_gate)
                {
                    _connection = connection;
                    _outbound = connection.Outbound.TryWrite;
                }
                await _client.AuthenticateAsync();
                lock (_gate)
                {
                    _attempt = 0;
                }
                failure = null;
                SetStatus(NanaLiveSessionStatus.Connected);
                SettleConnectWaiters();
                await connection.Completion;
            }
            catch (Exception error)
            {
                failure = error;
                lock (_gate)
                {
                    _outbound = null;
                    _connection = null;
                }
                if (connection is not null)
                {
                    try
                    {
                        await connection.CloseAsync();
                    }
                    catch (Exception)
                    {
                        // 忽略关闭失败。
                    }
                }
            }

            // 连接已断（或建立失败）：挂起中的请求立刻失败。
            _client.FailPending(new NanaLiveConnectionException("connection_lost"));

            lock (_gate)
            {
                if (_closed)
                {
                    break;
                }
            }
            if (!_options.Reconnect)
            {
                SetStatus(NanaLiveSessionStatus.Disconnected);
                FailConnectWaiters(ToFailure(failure));
                break;
            }
            lock (_gate)
            {
                _attempt += 1;
            }
            if (_options.MaxRetries is { } maxRetries && Volatile.Read(ref _attempt) > maxRetries)
            {
                SetStatus(NanaLiveSessionStatus.Disconnected);
                FailConnectWaiters(ToFailure(failure));
                break;
            }
            await Task.Delay(Backoff());
        }

        SetStatus(NanaLiveSessionStatus.Disconnected);
    }

    private static NanaLiveConnectionException ToFailure(Exception? failure) =>
        failure as NanaLiveConnectionException ?? new NanaLiveConnectionException(
            failure?.Message ?? "connection_lost");

    /// <summary>指数退避 + ±20% 抖动；按最近一次成功连接后的失败次数计。</summary>
    private TimeSpan Backoff()
    {
        int attempt;
        lock (_gate)
        {
            attempt = _attempt;
        }
        var baseDelay = _options.RetryDelay;
        for (var i = 1; i < attempt && baseDelay < _options.MaxRetryDelay; i++)
        {
            baseDelay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * 2);
        }
        baseDelay = TimeSpan.FromMilliseconds(
            Math.Min(baseDelay.TotalMilliseconds, _options.MaxRetryDelay.TotalMilliseconds));
        var jitter = baseDelay.TotalMilliseconds * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(
            Math.Max(0, baseDelay.TotalMilliseconds + jitter));
    }

    private void SetStatus(NanaLiveSessionStatus status)
    {
        Action<NanaLiveSessionStatus>? onStatus;
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }
            _status = status;
            onStatus = _options.OnStatus;
        }
        onStatus?.Invoke(status);
        if (status == NanaLiveSessionStatus.Connected)
        {
            SettleConnectWaiters();
        }
    }

    private void SettleConnectWaiters()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_gate)
        {
            waiters = _connectWaiters.ToArray();
            _connectWaiters.Clear();
        }
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    private void FailConnectWaiters(Exception error)
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_gate)
        {
            waiters = _connectWaiters.ToArray();
            _connectWaiters.Clear();
        }
        foreach (var waiter in waiters)
        {
            waiter.TrySetException(error);
        }
    }
}
