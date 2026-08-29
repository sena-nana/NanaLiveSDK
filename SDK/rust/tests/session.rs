use std::net::SocketAddr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use nanalive_sdk::*;
use rmpv::Value;
use tokio::net::TcpListener;
use tokio_tungstenite::accept_hdr_async;
use tokio_tungstenite::tungstenite::http::{Request, Response};
use tokio_tungstenite::tungstenite::Message;

fn encode(value: &Value) -> Vec<u8> {
    let mut bytes = Vec::new();
    rmpv::encode::write_value(&mut bytes, value).unwrap();
    bytes
}

fn decode(bytes: &[u8]) -> Value {
    rmpv::decode::read_value(&mut &bytes[..]).unwrap()
}

fn map(entries: &[(&str, Value)]) -> Value {
    Value::Map(
        entries
            .iter()
            .map(|(key, value)| (key.to_string().into(), value.clone()))
            .collect(),
    )
}

fn envelope(request_id: &str, response_type: &str, data: Value) -> Value {
    Value::Map(vec![
        ("apiName".into(), API_NAME.into()),
        ("apiVersion".into(), API_VERSION.into()),
        ("requestID".into(), request_id.into()),
        ("messageType".into(), response_type.into()),
        ("data".into(), data),
    ])
}

fn identity() -> Identity {
    Identity {
        plugin_id: "dev.example.plugin".into(),
        plugin_name: "Example".into(),
        plugin_developer: "Example".into(),
        plugin_version: "0.1.0".into(),
        scopes: vec!["model.read".into()],
    }
}

/// 服务端对 `AvailableModelsRequest` 的处理方式。
#[derive(Clone, Copy, PartialEq)]
enum ModelsBehavior {
    /// 正常回答。
    Answer,
    /// 先回答再断开第一条连接，模拟服务器崩溃（触发自动重连）。
    AnswerThenDropFirst,
    /// 不回答直接断开，模拟挂起中的请求在断线时失败。
    DropWithoutAnswer,
    /// 不回答也不断开，用于请求超时测试。
    SilentOnModels,
}

/// 返回 (requestID, messageType, 响应类型, 响应数据)。
fn route(request: &Value) -> Option<(String, String, String, Value)> {
    let request_id = request.get("requestID")?.as_str()?.to_string();
    let message_type = request.get("messageType")?.as_str()?.to_string();
    let (response_type, data) = match message_type.as_str() {
        "AuthenticationTokenRequest" => (
            "AuthenticationTokenResponse",
            map(&[("authenticationToken", "issued-token".into())]),
        ),
        "AuthenticationRequest" => ("AuthenticationResponse", map(&[])),
        "AvailableModelsRequest" => (
            "AvailableModelsResponse",
            map(&[("models", Value::Array(vec![map(&[("modelID", "m-1".into())])]))]),
        ),
        _ => return None,
    };
    Some((request_id, message_type, response_type.to_string(), data))
}

/// 本地 mock 服务端：循环接受多条连接（会话层重连会用）。
async fn spawn_mock_server(models_behavior: ModelsBehavior) -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let counter = Arc::new(AtomicUsize::new(0));
    tokio::spawn(async move {
        loop {
            let (stream, _) = match listener.accept().await {
                Ok(accepted) => accepted,
                Err(_) => return,
            };
            let index = counter.fetch_add(1, Ordering::SeqCst);
            tokio::spawn(async move {
                let callback = move |request: &Request<()>, mut response: Response<()>| {
                    if let Some(protocol) = request
                        .headers()
                        .get("Sec-WebSocket-Protocol")
                        .and_then(|value| value.to_str().ok())
                    {
                        // tungstenite 客户端要求服务端从请求的子协议中挑选一个回显。
                        response
                            .headers_mut()
                            .insert("Sec-WebSocket-Protocol", protocol.parse().unwrap());
                    }
                    Ok(response)
                };
                let mut websocket = match accept_hdr_async(stream, callback).await {
                    Ok(websocket) => websocket,
                    Err(_) => return,
                };
                while let Some(Ok(message)) = websocket.next().await {
                    let Message::Binary(payload) = message else {
                        continue;
                    };
                    let request = decode(&payload);
                    let Some((request_id, message_type, response_type, data)) =
                        route(&request)
                    else {
                        continue;
                    };
                    if message_type == "AvailableModelsRequest" {
                        match models_behavior {
                            ModelsBehavior::Answer => {}
                            ModelsBehavior::AnswerThenDropFirst if index == 0 => {
                                let response =
                                    envelope(&request_id, &response_type, data);
                                let _ = websocket
                                    .send(Message::Binary(encode(&response)))
                                    .await;
                                let _ = websocket.close(None).await;
                                return;
                            }
                            ModelsBehavior::AnswerThenDropFirst => {}
                            ModelsBehavior::DropWithoutAnswer => {
                                let _ = websocket.close(None).await;
                                return;
                            }
                            ModelsBehavior::SilentOnModels => continue,
                        }
                    }
                    let response = envelope(&request_id, &response_type, data);
                    if websocket
                        .send(Message::Binary(encode(&response)))
                        .await
                        .is_err()
                    {
                        return;
                    }
                }
            });
        }
    });
    addr
}

fn session_options(addr: SocketAddr, request_timeout: Option<Duration>) -> SessionOptions {
    SessionOptions {
        host: addr.ip().to_string(),
        port: addr.port(),
        identity: Some(identity()),
        retry_delay: Duration::from_millis(50),
        max_retry_delay: Duration::from_millis(100),
        heartbeat_interval: Duration::from_secs(1),
        request_timeout,
        ..SessionOptions::default()
    }
}

fn assert_models(response: &Value) {
    assert_eq!(
        response
            .get("data")
            .unwrap()
            .get("models")
            .unwrap()
            .as_array()
            .unwrap()[0]
            .get("modelID")
            .unwrap(),
        &Value::from("m-1")
    );
}

#[tokio::test]
async fn session_reconnects_after_server_drop() {
    let addr = spawn_mock_server(ModelsBehavior::AnswerThenDropFirst).await;
    let statuses = Arc::new(Mutex::new(Vec::new()));
    let captured = Arc::clone(&statuses);
    let session = Arc::new(NanaLiveSession::new(SessionOptions {
        on_status: Some(Arc::new(move |status| {
            captured.lock().unwrap().push(status);
        })),
        ..session_options(addr, Some(Duration::from_secs(5)))
    }));

    session.connect().await.unwrap();
    assert_models(&session.request("AvailableModelsRequest", Value::Map(vec![])).await.unwrap());

    // 第一条连接在回答后被服务端断开；重连后再次查询应成功。
    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    let mut reconnected = false;
    while tokio::time::Instant::now() < deadline {
        match session.request("AvailableModelsRequest", Value::Map(vec![])).await {
            Ok(models) => {
                assert_models(&models);
                reconnected = true;
                break;
            }
            Err(_) => tokio::time::sleep(Duration::from_millis(50)).await,
        }
    }
    assert!(reconnected, "重连后未能再次完成请求");
    session.close().await;

    let statuses = statuses.lock().unwrap();
    assert!(statuses.iter().filter(|s| **s == SessionStatus::Connected).count() >= 2);
    assert!(statuses.contains(&SessionStatus::Reconnecting));
    assert_eq!(*statuses.last().unwrap(), SessionStatus::Disconnected);
}

#[tokio::test]
async fn request_timeout_and_not_connected() {
    let addr = spawn_mock_server(ModelsBehavior::SilentOnModels).await;
    let session = Arc::new(NanaLiveSession::new(session_options(
        addr,
        Some(Duration::from_millis(400)),
    )));

    match session.request("AvailableModelsRequest", Value::Map(vec![])).await {
        Err(NanaLiveError::NotConnected) => {}
        other => panic!("expected NotConnected, got {other:?}"),
    }

    session.connect().await.unwrap();
    match session.request("AvailableModelsRequest", Value::Map(vec![])).await {
        Err(NanaLiveError::RequestTimeout) => {}
        other => panic!("expected RequestTimeout, got {other:?}"),
    }
    session.close().await;

    match session.request("AvailableModelsRequest", Value::Map(vec![])).await {
        Err(NanaLiveError::NotConnected) => {}
        other => panic!("expected NotConnected, got {other:?}"),
    }
}

#[tokio::test]
async fn pending_requests_fail_on_drop() {
    let addr = spawn_mock_server(ModelsBehavior::DropWithoutAnswer).await;
    let session = Arc::new(NanaLiveSession::new(session_options(
        addr,
        Some(Duration::from_secs(5)),
    )));

    session.connect().await.unwrap();
    match session.request("AvailableModelsRequest", Value::Map(vec![])).await {
        Err(NanaLiveError::ConnectionClosed(_)) => {}
        other => panic!("expected ConnectionClosed, got {other:?}"),
    }
    session.close().await;
}
