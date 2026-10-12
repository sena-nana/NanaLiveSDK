namespace Nanalive.Sdk;

/// <summary>服务端返回 <c>messageType == "APIError"</c> 时抛出。</summary>
public sealed class NanaLiveApiException : Exception
{
    /// <summary>服务端 <c>data.errorCode</c>。</summary>
    public object? Code { get; }

    public NanaLiveApiException(string message, object? code = null)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>鉴权时服务端没有签发 token。</summary>
public sealed class AuthenticationTokenMissingException : Exception
{
    public AuthenticationTokenMissingException()
        : base("authentication_token_missing")
    {
    }
}

/// <summary>连接层错误：未连接发起请求、断线后挂起请求失败等。</summary>
public class NanaLiveConnectionException : Exception
{
    public NanaLiveConnectionException(string message)
        : base(message)
    {
    }
}

/// <summary>请求在超时时间内没有等到响应。</summary>
public sealed class NanaLiveRequestTimeoutException : NanaLiveConnectionException
{
    public NanaLiveRequestTimeoutException()
        : base("request_timeout")
    {
    }
}

/// <summary>服务端 <c>APIError</c> 的 <c>data.errorCode</c>。</summary>
public static class ErrorCodes
{
    /// <summary>不支持的 apiVersion。</summary>
    public const string UnsupportedApiVersion = "UNSUPPORTED_API_VERSION";
    /// <summary>请求格式或字段无效。</summary>
    public const string InvalidRequest = "INVALID_REQUEST";
    /// <summary>消息超出大小上限。</summary>
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    /// <summary>需要先鉴权。</summary>
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    /// <summary>token 无效。</summary>
    public const string AuthenticationTokenInvalid = "AUTHENTICATION_TOKEN_INVALID";
    /// <summary>授权已被用户撤销。</summary>
    public const string AuthenticationRevoked = "AUTHENTICATION_REVOKED";
    /// <summary>用户拒绝。</summary>
    public const string UserDenied = "USER_DENIED";
    /// <summary>用户没有在时限内回答。</summary>
    public const string UserConfirmationTimeout = "USER_CONFIRMATION_TIMEOUT";
    /// <summary>授权里没有所需 scope。</summary>
    public const string PermissionDenied = "PERMISSION_DENIED";
    /// <summary>需要追加 scope。</summary>
    public const string ScopeUpgradeRequired = "SCOPE_UPGRADE_REQUIRED";
    /// <summary>舞台或窗口不可用。</summary>
    public const string DisplayUnavailable = "DISPLAY_UNAVAILABLE";
    /// <summary>舞台上没有模型。</summary>
    public const string ModelNotLoaded = "MODEL_NOT_LOADED";
    /// <summary>没有指定的模型。</summary>
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    /// <summary>正在换模型。</summary>
    public const string ModelSwitchInProgress = "MODEL_SWITCH_IN_PROGRESS";
    /// <summary>没有指定的表情。</summary>
    public const string ExpressionNotFound = "EXPRESSION_NOT_FOUND";
    /// <summary>没有指定的动作。</summary>
    public const string MotionNotFound = "MOTION_NOT_FOUND";
    /// <summary>没有指定的快捷键。</summary>
    public const string HotkeyNotFound = "HOTKEY_NOT_FOUND";
    /// <summary>快捷键不可执行。</summary>
    public const string HotkeyNotExecutable = "HOTKEY_NOT_EXECUTABLE";
    /// <summary>没有指定的挂件。</summary>
    public const string ItemNotFound = "ITEM_NOT_FOUND";
    /// <summary>模型没有指定的网格。</summary>
    public const string ArtMeshNotFound = "ART_MESH_NOT_FOUND";
    /// <summary>冷却未过（NDI 设置 3 秒一次、换模型）。</summary>
    public const string CooldownActive = "COOLDOWN_ACTIVE";
    /// <summary>已有问题在等用户回答。</summary>
    public const string PromptPending = "PROMPT_PENDING";
    /// <summary>另一个插件正持有它（物理覆盖）。</summary>
    public const string ControlledByOtherPlugin = "CONTROLLED_BY_OTHER_PLUGIN";
    /// <summary>本构建、本平台或当前版本不支持。</summary>
    public const string Unsupported = "UNSUPPORTED";
    /// <summary>目标正忙（如动作正在播放）。</summary>
    public const string ControlBusy = "CONTROL_BUSY";
    /// <summary>超出该类请求的限流配额。</summary>
    public const string RateLimited = "RATE_LIMITED";
    /// <summary>NanaLive 内部错误。</summary>
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>稍后重试可能成功的错误码，与 <c>data.retryable</c> 一致。</summary>
    public static readonly IReadOnlyCollection<string> Retryable = new HashSet<string>
    {
        UserConfirmationTimeout,
        DisplayUnavailable,
        ModelSwitchInProgress,
        CooldownActive,
        PromptPending,
        ControlledByOtherPlugin,
        ControlBusy,
        RateLimited,
        InternalError,
    };
}
