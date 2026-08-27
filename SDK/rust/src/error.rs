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
        }
    }
}

impl std::error::Error for NanaLiveError {}
