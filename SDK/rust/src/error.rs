use std::fmt;

/// 协议客户端的错误类型，语义与 JS SDK 抛出的错误一一对应。
#[derive(Debug, Clone, PartialEq)]
pub enum NanaLiveError {
    /// 服务端返回 `messageType == "APIError"`。
    Api {
        message: String,
        code: Option<rmpv::Value>,
    },
    /// 鉴权时服务端没有签发 token（对应 `authentication_token_missing`）。
    AuthenticationTokenMissing,
    /// 发送前编码 MessagePack 失败。
    Encode(String),
    /// 收到的字节无法解码为 MessagePack。
    Decode(String),
    /// WebSocket 连接建立失败。
    Connect(String),
    /// 连接在请求等待期间关闭或出错。
    ConnectionClosed(String),
    /// 会话未连接时发起请求。
    NotConnected,
    /// 请求在超时时间内没有等到响应。
    RequestTimeout,
    /// 建链、握手或鉴权超过了 `connect_timeout`。
    ConnectTimeout,
    /// 会话选项无效（如 `retry_delay` 为零或大于上限）。
    InvalidOption(String),
}

impl NanaLiveError {
    /// `APIError` 响应中的 `data.errorCode`。
    pub fn code(&self) -> Option<&rmpv::Value> {
        match self {
            NanaLiveError::Api { code, .. } => code.as_ref(),
            _ => None,
        }
    }
}

impl fmt::Display for NanaLiveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            NanaLiveError::Api { message, code } => match code {
                Some(code) => write!(f, "{message} (errorCode={code})"),
                None => write!(f, "{message}"),
            },
            NanaLiveError::AuthenticationTokenMissing => {
                write!(f, "authentication_token_missing")
            }
            NanaLiveError::Encode(message) => write!(f, "encode_failed: {message}"),
            NanaLiveError::Decode(message) => write!(f, "decode_failed: {message}"),
            NanaLiveError::Connect(message) => write!(f, "connect_failed: {message}"),
            NanaLiveError::ConnectionClosed(message) => {
                write!(f, "connection_closed: {message}")
            }
            NanaLiveError::NotConnected => write!(f, "not_connected"),
            NanaLiveError::RequestTimeout => write!(f, "request_timeout"),
            NanaLiveError::ConnectTimeout => write!(f, "connect_timeout"),
            NanaLiveError::InvalidOption(message) => write!(f, "invalid_option: {message}"),
        }
    }
}

impl std::error::Error for NanaLiveError {}

/// 服务端 `APIError` 的 `data.errorCode`。
pub mod error_code {
    /// 不支持的 apiVersion。
    pub const UNSUPPORTED_API_VERSION: &str = "UNSUPPORTED_API_VERSION";
    /// 请求格式或字段无效。
    pub const INVALID_REQUEST: &str = "INVALID_REQUEST";
    /// 消息超出大小上限。
    pub const MESSAGE_TOO_LARGE: &str = "MESSAGE_TOO_LARGE";
    /// 需要先鉴权。
    pub const AUTHENTICATION_REQUIRED: &str = "AUTHENTICATION_REQUIRED";
    /// token 无效。
    pub const AUTHENTICATION_TOKEN_INVALID: &str = "AUTHENTICATION_TOKEN_INVALID";
    /// 授权已被用户撤销。
    pub const AUTHENTICATION_REVOKED: &str = "AUTHENTICATION_REVOKED";
    /// 用户拒绝。
    pub const USER_DENIED: &str = "USER_DENIED";
    /// 用户没有在时限内回答。
    pub const USER_CONFIRMATION_TIMEOUT: &str = "USER_CONFIRMATION_TIMEOUT";
    /// 授权里没有所需 scope。
    pub const PERMISSION_DENIED: &str = "PERMISSION_DENIED";
    /// 需要追加 scope。
    pub const SCOPE_UPGRADE_REQUIRED: &str = "SCOPE_UPGRADE_REQUIRED";
    /// 舞台或窗口不可用。
    pub const DISPLAY_UNAVAILABLE: &str = "DISPLAY_UNAVAILABLE";
    /// 舞台上没有模型。
    pub const MODEL_NOT_LOADED: &str = "MODEL_NOT_LOADED";
    /// 没有指定的模型。
    pub const MODEL_NOT_FOUND: &str = "MODEL_NOT_FOUND";
    /// 正在换模型。
    pub const MODEL_SWITCH_IN_PROGRESS: &str = "MODEL_SWITCH_IN_PROGRESS";
    /// 没有指定的表情。
    pub const EXPRESSION_NOT_FOUND: &str = "EXPRESSION_NOT_FOUND";
    /// 没有指定的动作。
    pub const MOTION_NOT_FOUND: &str = "MOTION_NOT_FOUND";
    /// 没有指定的快捷键。
    pub const HOTKEY_NOT_FOUND: &str = "HOTKEY_NOT_FOUND";
    /// 快捷键不可执行。
    pub const HOTKEY_NOT_EXECUTABLE: &str = "HOTKEY_NOT_EXECUTABLE";
    /// 没有指定的挂件。
    pub const ITEM_NOT_FOUND: &str = "ITEM_NOT_FOUND";
    /// 模型没有指定的网格。
    pub const ART_MESH_NOT_FOUND: &str = "ART_MESH_NOT_FOUND";
    /// 冷却未过（NDI 设置 3 秒一次、换模型）。
    pub const COOLDOWN_ACTIVE: &str = "COOLDOWN_ACTIVE";
    /// 已有问题在等用户回答。
    pub const PROMPT_PENDING: &str = "PROMPT_PENDING";
    /// 另一个插件正持有它（物理覆盖）。
    pub const CONTROLLED_BY_OTHER_PLUGIN: &str = "CONTROLLED_BY_OTHER_PLUGIN";
    /// 本构建、本平台或当前版本不支持。
    pub const UNSUPPORTED: &str = "UNSUPPORTED";
    /// 目标正忙（如动作正在播放）。
    pub const CONTROL_BUSY: &str = "CONTROL_BUSY";
    /// 超出该类请求的限流配额。
    pub const RATE_LIMITED: &str = "RATE_LIMITED";
    /// NanaLive 内部错误。
    pub const INTERNAL_ERROR: &str = "INTERNAL_ERROR";

    /// 稍后重试可能成功的错误码，与 `data.retryable` 一致。
    pub const RETRYABLE: &[&str] = &[
        USER_CONFIRMATION_TIMEOUT,
        DISPLAY_UNAVAILABLE,
        MODEL_SWITCH_IN_PROGRESS,
        COOLDOWN_ACTIVE,
        PROMPT_PENDING,
        CONTROLLED_BY_OTHER_PLUGIN,
        CONTROL_BUSY,
        RATE_LIMITED,
        INTERNAL_ERROR,
    ];
}
