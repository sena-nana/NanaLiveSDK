//! 基于 tokio-tungstenite 的 WebSocket 连接帮手，对应 JS SDK 的
//! `connectBinaryWebSocket` 用法（子协议 + 二进制 MessagePack 帧）。

use std::sync::Arc;

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

pub type UnhandledCallback = Arc<dyn Fn(Value) + Send + Sync>;
pub type ErrorCallback = Arc<dyn Fn(String) + Send + Sync>;

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

    pub fn into_parts(self) -> (Arc<NanaLiveClient>, JoinHandle<()>) {
        (self.client, self.task)
    }
}

/// 连接 NanaLive 控制 API，返回客户端与后台泵任务。
///
/// 泵任务把入站 MessagePack 帧喂给 [`NanaLiveClient::receive`]，
/// 客户端的 `send` 回调经出站通道写回 WebSocket。
pub async fn connect(options: ConnectOptions) -> Result<ConnectionHandle, NanaLiveError> {
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

    let (outbound, mut outbound_rx) = mpsc::unbounded_channel::<Message>();
    let outbound_for_send = outbound.clone();
    let client = Arc::new(NanaLiveClient::new(
        move |bytes: Vec<u8>| {
            let _ = outbound_for_send.send(Message::Binary(bytes));
        },
        options.identity,
        options.token,
        options.on_token,
    ));

    let pump_client = Arc::clone(&client);
    let on_unhandled = options.on_unhandled;
    let on_error = options.on_error;
    let task = tokio::spawn(async move {
        loop {
            tokio::select! {
                inbound = stream.next() => match inbound {
                    Some(Ok(Message::Binary(payload))) => match pump_client.receive(&payload) {
                        Ok(Some(value)) => {
                            if let Some(on_unhandled) = &on_unhandled {
                                on_unhandled(value);
                            }
                        }
                        Ok(None) => {}
                        Err(error) => {
                            if let Some(on_error) = &on_error {
                                on_error(error.to_string());
                            }
                        }
                    },
                    // tungstenite 不会自动回 Pong，这里显式回应。
                    Some(Ok(Message::Ping(payload))) => {
                        let _ = sink.send(Message::Pong(payload)).await;
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
                            on_error(error.to_string());
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
    });

    Ok(ConnectionHandle {
        client,
        task,
        outbound,
    })
}
