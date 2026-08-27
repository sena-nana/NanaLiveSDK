use std::sync::Arc;
use std::time::Duration;

use nanalive_sdk::*;
use rmpv::Value;

fn encode(value: &Value) -> Vec<u8> {
    let mut bytes = Vec::new();
    rmpv::encode::write_value(&mut bytes, value).unwrap();
    bytes
}

fn decode(bytes: &[u8]) -> Value {
    rmpv::decode::read_value(&mut &bytes[..]).unwrap()
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

struct Mock {
    client: Arc<NanaLiveClient>,
    rx: std::sync::mpsc::Receiver<Vec<u8>>,
}

fn setup(
    identity: Option<Identity>,
    token: Option<String>,
    on_token: Option<nanalive_sdk::client::TokenCallback>,
) -> Mock {
    let (tx, rx) = std::sync::mpsc::channel();
    let client = Arc::new(NanaLiveClient::new(
        move |bytes| tx.send(bytes).unwrap(),
        identity,
        token,
        on_token,
    ));
    Mock { client, rx }
}

impl Mock {
    /// 等待客户端发出的下一条消息（轮询避免阻塞 tokio 运行时）。
    async fn next_sent(&self) -> Value {
        loop {
            if let Ok(bytes) = self.rx.try_recv() {
                return decode(&bytes);
            }
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
    }
}

fn map(entries: &[(&str, Value)]) -> Value {
    Value::Map(
        entries
            .iter()
            .map(|(key, value)| (key.to_string().into(), value.clone()))
            .collect(),
    )
}

#[tokio::test]
async fn envelope_has_fixed_fields_and_increasing_request_ids() {
    let mock = setup(Some(Identity::default()), None, None);
    let pending = tokio::spawn({
        let client = Arc::clone(&mock.client);
        async move { client.request("AvailableModelsRequest", map(&[])).await }
    });
    let first = mock.next_sent().await;

    assert_eq!(first.get("apiName").unwrap(), &Value::from(API_NAME));
    assert_eq!(first.get("apiVersion").unwrap(), &Value::from(API_VERSION));
    assert_eq!(first.get("messageType").unwrap(), &Value::from("AvailableModelsRequest"));
    assert_eq!(first.get("data").unwrap(), &map(&[]));
    let first_id = first.get("requestID").unwrap().as_str().unwrap().to_string();
    pending.abort();

    let pending = tokio::spawn({
        let client = Arc::clone(&mock.client);
        async move { client.request("MotionListRequest", map(&[])).await }
    });
    let second = mock.next_sent().await;
    let second_id = second.get("requestID").unwrap().as_str().unwrap().to_string();
    pending.abort();

    assert_eq!(first_id, "nanalive-1");
    assert_eq!(second_id, "nanalive-2");
}

#[tokio::test]
async fn response_is_paired_and_unpaired_push_is_passed_through() {
    let mock = setup(None, None, None);
    let client = Arc::clone(&mock.client);
    let pending = tokio::spawn(async move {
        client.request("HotkeyListRequest", map(&[])).await
    });

    let sent = mock.next_sent().await;
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();

    // 无匹配等待者的推送先到达，应原样透传。
    let push = envelope("nanalive-other", "SomePush", map(&[("n", 1.into())]));
    assert_eq!(mock.client.receive(&encode(&push)).unwrap(), Some(push));
    // 配对响应。
    let response = envelope(&request_id, "HotkeyListResponse", map(&[("hotkeys", Value::Array(vec![]))]));
    assert_eq!(mock.client.receive(&encode(&response)).unwrap(), None);

    let result = pending.await.unwrap().unwrap();
    assert_eq!(result.get("data").unwrap(), &map(&[("hotkeys", Value::Array(vec![]))]));
}

#[tokio::test]
async fn api_error_response_rejects_with_code() {
    let mock = setup(None, None, None);
    let client = Arc::clone(&mock.client);
    let pending = tokio::spawn(async move {
        client.request("MotionTriggerRequest", map(&[("motionID", "m1".into())])).await
    });

    let sent = mock.next_sent().await;
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let error = envelope(
        &request_id,
        "APIError",
        map(&[("message", "motion not found".into()), ("errorCode", "motion_not_found".into())]),
    );
    mock.client.receive(&encode(&error)).unwrap();

    let error = pending.await.unwrap().unwrap_err();
    match &error {
        NanaLiveError::Api { message, code } => {
            assert_eq!(message, "motion not found");
            assert_eq!(code, &Some(Value::from("motion_not_found")));
        }
        other => panic!("expected Api error, got {other:?}"),
    }
    assert_eq!(error.code(), Some(&Value::from("motion_not_found")));
}

#[tokio::test]
async fn authenticate_with_valid_token_only_verifies_once() {
    let mock = setup(None, Some("saved-token".into()), None);
    let client = Arc::clone(&mock.client);
    let pending = tokio::spawn(async move { client.authenticate().await });

    let sent = mock.next_sent().await;
    assert_eq!(sent.get("messageType").unwrap(), &Value::from("AuthenticationRequest"));
    assert_eq!(
        sent.get("data").unwrap().get("authenticationToken").unwrap(),
        &Value::from("saved-token")
    );

    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let response = envelope(&request_id, "AuthenticationResponse", map(&[]));
    mock.client.receive(&encode(&response)).unwrap();
    pending.await.unwrap().unwrap();
}

#[tokio::test]
async fn authenticate_falls_back_when_saved_token_is_rejected() {
    let issued_tokens = Arc::new(std::sync::Mutex::new(Vec::new()));
    let captured = Arc::clone(&issued_tokens);
    let mock = setup(
        Some(Identity {
            plugin_id: "dev.example.plugin".into(),
            plugin_name: "Example".into(),
            plugin_developer: "Example".into(),
            plugin_version: "0.1.0".into(),
            scopes: vec!["model.read".into()],
        }),
        Some("stale-token".into()),
        Some(Arc::new(move |token: &str| captured.lock().unwrap().push(token.to_string()))),
    );

    let client = Arc::clone(&mock.client);
    let pending = tokio::spawn(async move { client.authenticate().await });

    // 第一步：旧 token 验证被拒。
    let sent = mock.next_sent().await;
    assert_eq!(sent.get("messageType").unwrap(), &Value::from("AuthenticationRequest"));
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let error = envelope(&request_id, "APIError", map(&[("message", "invalid token".into())]));
    mock.client.receive(&encode(&error)).unwrap();

    // 第二步：降级申请新 token。
    let sent = mock.next_sent().await;
    assert_eq!(sent.get("messageType").unwrap(), &Value::from("AuthenticationTokenRequest"));
    assert_eq!(
        sent.get("data").unwrap().get("pluginID").unwrap(),
        &Value::from("dev.example.plugin")
    );
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let response = envelope(
        &request_id,
        "AuthenticationTokenResponse",
        map(&[("authenticationToken", "fresh-token".into())]),
    );
    mock.client.receive(&encode(&response)).unwrap();

    // 第三步：用新 token 验证。
    let sent = mock.next_sent().await;
    assert_eq!(sent.get("messageType").unwrap(), &Value::from("AuthenticationRequest"));
    assert_eq!(
        sent.get("data").unwrap().get("authenticationToken").unwrap(),
        &Value::from("fresh-token")
    );
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let response = envelope(&request_id, "AuthenticationResponse", map(&[]));
    mock.client.receive(&encode(&response)).unwrap();

    pending.await.unwrap().unwrap();
    assert_eq!(*issued_tokens.lock().unwrap(), vec!["fresh-token".to_string()]);
}

#[tokio::test]
async fn authenticate_fails_when_no_token_is_issued() {
    let mock = setup(None, None, None);
    let client = Arc::clone(&mock.client);
    let pending = tokio::spawn(async move { client.authenticate().await });

    let sent = mock.next_sent().await;
    let request_id = sent.get("requestID").unwrap().as_str().unwrap().to_string();
    let response = envelope(&request_id, "AuthenticationTokenResponse", map(&[]));
    mock.client.receive(&encode(&response)).unwrap();

    assert_eq!(
        pending.await.unwrap().unwrap_err(),
        NanaLiveError::AuthenticationTokenMissing
    );
}

#[test]
fn executable_hotkeys_filters_on_executable_flag() {
    let hotkeys = vec![
        map(&[("hotkeyID", "h1".into()), ("executable", Value::Boolean(true))]),
        map(&[("hotkeyID", "h2".into()), ("executable", Value::Boolean(false))]),
        map(&[("hotkeyID", "h3".into())]),
    ];
    let executable = executable_hotkeys(&hotkeys);
    assert_eq!(executable.len(), 1);
    assert_eq!(executable[0].get("hotkeyID").unwrap(), &Value::from("h1"));
}

#[test]
fn parameter_value_after_ticks_clamps_to_range() {
    // 每格 0.5（量程 0..20 除以 40）。
    let parameter = map(&[("value", 10.0.into()), ("min", 0.0.into()), ("max", 20.0.into())]);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), 0.0), 10.0);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), 4.0), 12.0);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), -4.0), 8.0);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), 400.0), 20.0);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), -400.0), 0.0);
    assert_eq!(parameter_value_after_ticks(Some(&parameter), f64::NAN), 10.0);
    // 无参数回退 0，span 为 0 时步长为 1。
    assert_eq!(parameter_value_after_ticks(None, 3.0), 0.0);
    let flat = map(&[("value", 7.0.into()), ("min", 5.0.into()), ("max", 5.0.into())]);
    // span 为 0 时步长为 1，但仍钳制在 min==max 上。
    assert_eq!(parameter_value_after_ticks(Some(&flat), 2.0), 5.0);
}

#[test]
fn write_parameter_command_validates_input() {
    let command = write_parameter_command(Some("ParamA"), 3.5).unwrap();
    assert_eq!(command.get("messageType").unwrap(), &Value::from("ParameterWriteRequest"));
    let parameters = command.get("data").unwrap().get("parameters").unwrap();
    assert_eq!(parameters.get("ParamA").unwrap(), &Value::from(3.5));

    assert!(write_parameter_command(None, 1.0).is_none());
    assert!(write_parameter_command(Some(""), 1.0).is_none());
    assert!(write_parameter_command(Some("ParamA"), f64::NAN).is_none());
}
