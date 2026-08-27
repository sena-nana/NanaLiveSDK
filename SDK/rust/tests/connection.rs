use std::net::SocketAddr;
use std::sync::{Arc, Mutex};

use futures_util::{SinkExt, StreamExt};
use nanalive_sdk::*;
use rmpv::Value;
use tokio::net::TcpListener;
use tokio_tungstenite::tungstenite::http::{Request, Response};
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::accept_hdr_async;
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

fn envelope(request_id: &str, message_type: &str, data: Value) -> Value {
    Value::Map(vec![
        ("apiName".into(), API_NAME.into()),
        ("apiVersion".into(), API_VERSION.into()),
        ("requestID".into(), request_id.into()),
        ("messageType".into(), message_type.into()),
        ("data".into(), data),
    ])
}

/// 起一个本地 mock 服务端：回答鉴权与模型目录请求，并记录请求到的子协议。
#[allow(clippy::result_large_err)]
async fn spawn_mock_server() -> (SocketAddr, Arc<Mutex<Option<String>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let seen_subprotocol = Arc::new(Mutex::new(None::<String>));
    let captured = Arc::clone(&seen_subprotocol);
    tokio::spawn(async move {
        let (stream, _) = listener.accept().await.unwrap();
        let callback = move |request: &Request<()>, mut response: Response<()>| {
            if let Some(protocol) = request
                .headers()
                .get("Sec-WebSocket-Protocol")
                .and_then(|value| value.to_str().ok())
            {
                *captured.lock().unwrap() = Some(protocol.to_string());
                // tungstenite 客户端要求服务端从请求的子协议中挑选一个回显。
                response
                    .headers_mut()
                    .insert("Sec-WebSocket-Protocol", protocol.parse().unwrap());
            }
            Ok(response)
        };
        let mut websocket = accept_hdr_async(stream, callback).await.unwrap();
        while let Some(Ok(message)) = websocket.next().await {
            match message {
                Message::Binary(payload) => {
                    let request = decode(&payload);
                    let request_id = request.get("requestID").unwrap().as_str().unwrap();
                    let message_type = request.get("messageType").unwrap().as_str().unwrap();
                    let (response_type, data) = match message_type {
                        "AuthenticationTokenRequest" => (
                            "AuthenticationTokenResponse",
                            map(&[("authenticationToken", "issued-token".into())]),
                        ),
                        "AuthenticationRequest" => ("AuthenticationResponse", map(&[])),
                        "AvailableModelsRequest" => (
                            "AvailableModelsResponse",
                            map(&[("models", Value::Array(vec![map(&[("modelID", "m-1".into())])]))]),
                        ),
                        _ => continue,
                    };
                    let response = envelope(request_id, response_type, data);
                    websocket.send(Message::Binary(encode(&response))).await.unwrap();
                }
                Message::Close(_) => break,
                _ => {}
            }
        }
    });
    (addr, seen_subprotocol)
}

#[tokio::test]
async fn connect_authenticate_and_list_models() {
    let (addr, seen_subprotocol) = spawn_mock_server().await;

    let handle = connect(ConnectOptions {
        host: addr.ip().to_string(),
        port: addr.port(),
        identity: Some(Identity {
            plugin_id: "dev.example.plugin".into(),
            plugin_name: "Example".into(),
            plugin_developer: "Example".into(),
            plugin_version: "0.1.0".into(),
            scopes: vec!["model.read".into()],
        }),
        on_token: Some(Arc::new(|token| {
            assert_eq!(token, "issued-token");
        })),
        ..ConnectOptions::new()
    })
    .await
    .unwrap();

    handle.client.authenticate().await.unwrap();
    let models = handle.client.list_models().await.unwrap();
    let first = &models.get("data").unwrap().get("models").unwrap().as_array().unwrap()[0];
    assert_eq!(first.get("modelID").unwrap(), &Value::from("m-1"));

    assert_eq!(
        seen_subprotocol.lock().unwrap().as_deref(),
        Some(SUBPROTOCOL)
    );

    handle.close().await;
    handle.task.await.unwrap();
}
