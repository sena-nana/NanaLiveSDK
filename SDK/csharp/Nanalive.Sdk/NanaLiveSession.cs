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
    private NanaLiveConnection? _connection;
    private Func<byte[], bool>? _outbound;
    private NanaLiveSessionStatus _status = NanaLiveSessionStatus.Disconnected;
    private bool _closed;
    private Task? _supervisor;
    private CancellationTokenSource? _supervisorCts;

    /// <summary>创建会话；需要连接时调用 <see cref="ConnectAsync(CancellationToken)"/>。</summary>
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

    /// <summary>建立会话（内联重试直到首个连接完成鉴权）。</summary>
    /// <remarks>
    /// 之后的断线由后台任务自动重连；重复调用会重置会话并重新连接。
    /// 重试耗尽（或 <see cref="SessionOptions.Reconnect"/> 为 <c>false</c>
    /// 且连不上）时抛出最后的错误。
    /// </remarks>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await StopSupervisorAsync();
        _closed = false;

        var attempt = 0;
        while (true)
        {
            SetStatus(attempt == 0
                ? NanaLiveSessionStatus.Connecting
                : NanaLiveSessionStatus.Reconnecting);
            NanaLiveConnection connection;
            try
            {
                connection = await EstablishAsync();
            }
            catch (Exception)
            {
                attempt += 1;
                if (_closed
                    || !_options.Reconnect
                    || (_options.MaxRetries is { } maxRetries && attempt > maxRetries))
                {
                    SetStatus(NanaLiveSessionStatus.Disconnected);
                    throw;
                }
                await Task.Delay(Backoff(attempt), cancellationToken);
                continue;
            }
            if (_closed)
            {
                try { await connection.CloseAsync(); }
                catch (Exception) { /* 忽略关闭失败 */ }
                throw new NanaLiveConnectionException("closed");
            }
            StartSupervisor(connection);
            return;
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
        if (_closed)
        {
            return;
        }
        _closed = true;
        await StopSupervisorAsync();

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
        _client.FailPending(new NanaLiveConnectionException("connection_lost"));
        SetStatus(NanaLiveSessionStatus.Disconnected);
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    // 建立一次连接并完成鉴权；失败时清理半开连接后向上抛。
    private async Task<NanaLiveConnection> EstablishAsync()
    {
        var connection = await NanaLiveConnection.ConnectAsync(new ConnectOptions
        {
            Host = _options.Host,
            Port = _options.Port,
            KeepAliveInterval = _options.HeartbeatInterval,
            OnUnhandled = _options.OnUnhandled,
            OnError = _options.OnError,
        }, _client);
        lock (_gate)
        {
            _connection = connection;
            _outbound = connection.Outbound.TryWrite;
        }
        try
        {
            await _client.AuthenticateAsync();
        }
        catch
        {
            lock (_gate)
            {
                if (_connection == connection)
                {
                    _connection = null;
                    _outbound = null;
                }
            }
            try { await connection.CloseAsync(); }
            catch (Exception) { /* 忽略关闭失败 */ }
            throw;
        }
        SetStatus(NanaLiveSessionStatus.Connected);
        return connection;
    }

    // 断开后的后台重连循环；close() 通过 CTS 终止本任务。
    private void StartSupervisor(NanaLiveConnection connection)
    {
        _supervisorCts = new CancellationTokenSource();
        var cancellationToken = _supervisorCts.Token;
        _supervisor = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // WaitAsync 让 close() 的取消能立刻打断等待。
                        await connection.Completion.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // 断线原因已通过 OnError 上报。
                    }

                    _client.FailPending(new NanaLiveConnectionException("connection_lost"));
                    lock (_gate)
                    {
                        if (_connection == connection)
                        {
                            _connection = null;
                            _outbound = null;
                        }
                    }
                    if (cancellationToken.IsCancellationRequested || _closed)
                    {
                        return;
                    }
                    if (!_options.Reconnect)
                    {
                        SetStatus(NanaLiveSessionStatus.Disconnected);
                        return;
                    }

                    var attempt = 0;
                    while (true)
                    {
                        attempt += 1;
                        if (_options.MaxRetries is { } maxRetries && attempt > maxRetries)
                        {
                            SetStatus(NanaLiveSessionStatus.Disconnected);
                            return;
                        }
                        SetStatus(NanaLiveSessionStatus.Reconnecting);
                        try
                        {
                            await Task.Delay(Backoff(attempt), cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        if (cancellationToken.IsCancellationRequested || _closed)
                        {
                            return;
                        }
                        try
                        {
                            connection = await EstablishAsync();
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch
                        {
                            if (cancellationToken.IsCancellationRequested || _closed)
                            {
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                SetStatus(NanaLiveSessionStatus.Disconnected);
            }
        });
    }

    private async Task StopSupervisorAsync()
    {
        var cts = _supervisorCts;
        var supervisor = _supervisor;
        _supervisorCts = null;
        _supervisor = null;
        if (cts is not null)
        {
            cts.Cancel();
        }
        if (supervisor is not null)
        {
            try
            {
                await supervisor;
            }
            catch (Exception)
            {
                // 监督任务的异常不影响调用方。
            }
        }
        cts?.Dispose();
    }

    // 指数退避 + ±20% 抖动；attempt 从 1 计。
    private TimeSpan Backoff(int attempt)
    {
        var baseDelay = Math.Min(
            _options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
            _options.MaxRetryDelay.TotalMilliseconds);
        var jitter = baseDelay * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseDelay + jitter));
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
    }
}
