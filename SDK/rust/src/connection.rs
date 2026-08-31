//! 基于 tokio-tungstenite 的 WebSocket 连接帮手，对应 JS SDK 的
//! `connectBinaryWebSocket` 用法（子协议 + 二进制 MessagePack 帧）。

use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use futures_util::{SinkExt, StreamExt};
use rmpv::Value;
use tokio::sync::mpsc;
use tokio::task::JoinHandle;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::connect_async;

use crate::client::{Identity, NanaLiveClient, TokenCallback};
use crate::error::NanaLiveError;
use crate::{DEFAULT_PORT, SUBPROTOCOL};

/// 用户回调的 panic 隔离：回调崩了不能带走泵任务。
pub(crate) fn guard<F: FnOnce()>(f: F) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(f));
}

pub type UnhandledCallback = Arc<dyn Fn(Value) + Send + Sync>;
pub type ErrorCallback = Arc<dyn Fn(String) + Send + Sync>;
/// 收到任意入站帧（数据/ping/pong）时触发，会话层用它做活动时间戳。
pub type ActivityCallback = Arc<dyn Fn() + Send + Sync>;
/// 泵任务退出（连接断开）时触发，会话层据此发起重连。
pub type DisconnectCallback = Arc<dyn Fn() + Send + Sync>;

#[derive(Clone, Default)]
pub struct ConnectOptions {
    /// 默认 `127.0.0.1`。
    pub host: String,
    /// 默认 [`DEFAULT_PORT`]（8312）。
    pub port: u16,
    pub identity: Option<Identity>,
    pub token: Option<String>,
    /// 首次签发的 token，用于调用方持久化。
    pub on_token: Option<TokenCallback>,
    /// 未配对请求的响应（服务器主动推送）。
    pub on_unhandled: Option<UnhandledCallback>,
    /// 泵任务中的协议/连接错误。
    pub on_error: Option<ErrorCallback>,
    /// 收到任意入站帧时的活动回调。
    pub on_activity: Option<ActivityCallback>,
    /// 泵任务退出（连接断开）时的回调。
    pub on_disconnect: Option<DisconnectCallback>,
}

impl ConnectOptions {
    pub fn new() -> Self {
        Self {
            host: "127.0.0.1".to_string(),
            port: DEFAULT_PORT,
            identity: None,
            token: None,
            on_token: None,
            on_unhandled: None,
            on_error: None,
            on_activity: None,
            on_disconnect: None,
        }
    }
}

/// [`connect`] 的返回值：客户端 + 泵任务。
pub struct ConnectionHandle {
    pub client: Arc<NanaLiveClient>,
    pub task: JoinHandle<()>,
    outbound: mpsc::UnboundedSender<Message>,
}

impl ConnectionHandle {
    /// 发起优雅关闭，随后可 `await task` 等泵任务退出。
    pub async fn close(&self) {
        let _ = self.outbound.send(Message::Close(None));
    }

    /// 通过出站通道发送一条原始 WebSocket 消息，返回是否入队成功。
    pub fn send_raw(&self, message: Message) -> bool {
        self.outbound.send(message).is_ok()
    }

    /// 发送一条 WebSocket ping（会话层心跳）。
    pub fn ping(&self) -> bool {
        self.send_raw(Message::Ping(Vec::new()))
    }

    pub fn into_parts(self) -> (Arc<NanaLiveClient>, JoinHandle<()>) {
        (self.client, self.task)
    }
}

/// 连接 NanaLive 控制 API，返回客户端与后台泵任务。
///
/// 泵任务把入站 MessagePack 帧喂给 [`NanaLiveClient::receive`]，
/// 客户端的 `send` 回调经出站通道写回 WebSocket。
pub async fn connect(options: ConnectOptions) -> Result<ConnectionHandle, NanaLiveError> {
    let ConnectOptions {
        identity,
        token,
        on_token,
        ..
    } = options.clone();
    let (outbound, outbound_rx) = mpsc::unbounded_channel::<Message>();
    let outbound_for_send = outbound.clone();
    let client = Arc::new(NanaLiveClient::new(
        move |bytes: Vec<u8>| {
            outbound_for_send
                .send(Message::Binary(bytes))
                .map_err(|_| NanaLiveError::ConnectionClosed("connection_lost".to_string()))
        },
        identity,
        token,
        on_token,
    ));
    establish_connection(options, client, outbound, outbound_rx).await
}

/// 同 [`connect`]，但复用调用方提供的客户端（会话层跨重连共享 token 与等待队列）。
pub async fn connect_with_client(
    options: ConnectOptions,
    client: Arc<NanaLiveClient>,
) -> Result<ConnectionHandle, NanaLiveError> {
    let (outbound, outbound_rx) = mpsc::unbounded_channel::<Message>();
    establish_connection(options, client, outbound, outbound_rx).await
}

async fn establish_connection(
    options: ConnectOptions,
    client: Arc<NanaLiveClient>,
    outbound: mpsc::UnboundedSender<Message>,
    mut outbound_rx: mpsc::UnboundedReceiver<Message>,
) -> Result<ConnectionHandle, NanaLiveError> {
    let url = format!("ws://{}:{}/", options.host, options.port);
    let mut request = url
        .into_client_request()
        .map_err(|error| NanaLiveError::Connect(error.to_string()))?;
    request.headers_mut().insert(
        "Sec-WebSocket-Protocol",
        HeaderValue::from_static(SUBPROTOCOL),
    );
    let (websocket, _response) = connect_async(request)
        .await
        .map_err(|error| NanaLiveError::Connect(error.to_string()))?;
    let (mut sink, mut stream) = websocket.split();

    let pump_client = Arc::clone(&client);
    let on_unhandled = options.on_unhandled;
    let on_error = options.on_error;
    let on_activity = options.on_activity;
    let on_disconnect = options.on_disconnect;
    let task = tokio::spawn(async move {
        loop {
            tokio::select! {
                inbound = stream.next() => match inbound {
                    Some(Ok(Message::Binary(payload))) => {
                        if let Some(on_activity) = &on_activity {
                            guard(|| on_activity());
                        }
                        match pump_client.receive(&payload) {
                            Ok(Some(value)) => {
                                if let Some(on_unhandled) = &on_unhandled {
                                    guard(move || on_unhandled(value));
                                }
                            }
                            Ok(None) => {}
                            Err(error) => {
                                if let Some(on_error) = &on_error {
                                    guard(move || on_error(error.to_string()));
                                }
                            }
                        }
                    }
                    Some(Ok(Message::Ping(payload))) => {
                        if let Some(on_activity) = &on_activity {
                            guard(|| on_activity());
                        }
                        // tungstenite 不会自动回 Pong，这里显式回应。
                        let _ = sink.send(Message::Pong(payload)).await;
                    }
                    Some(Ok(Message::Pong(_))) => {
                        if let Some(on_activity) = &on_activity {
                            guard(|| on_activity());
                        }
                    }
                    Some(Ok(Message::Close(frame))) => {
                        let _ = sink.send(Message::Close(frame)).await;
                        break;
                    }
                    Some(Ok(_)) => {}
                    None => {
                        let _ = sink.send(Message::Close(None)).await;
                        break;
                    }
                    Some(Err(error)) => {
                        if let Some(on_error) = &on_error {
                            guard(move || on_error(error.to_string()));
                        }
                        let _ = sink.close().await;
                        break;
                    }
                },
                outbound = outbound_rx.recv() => match outbound {
                    Some(Message::Close(frame)) => {
                        let _ = sink.send(Message::Close(frame)).await;
                        break;
                    }
                    Some(message) => {
                        if sink.send(message).await.is_err() {
                            break;
                        }
                    }
                    None => {
                        let _ = sink.close().await;
                        break;
                    }
                },
            }
        }
        if let Some(on_disconnect) = &on_disconnect {
            guard(|| on_disconnect());
        }
    });

    Ok(ConnectionHandle {
        client,
        task,
        outbound,
    })
}

/// 当前 UNIX 时间（毫秒），会话层用它记录活动时间戳。
pub(crate) fn now_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as u64)
        .unwrap_or(0)
}
