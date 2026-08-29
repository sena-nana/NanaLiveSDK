//! 与传输无关的协议客户端，对应 JS SDK 的 `createNanaLiveClient`。

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use rmpv::Value;
use tokio::sync::oneshot;

use crate::error::NanaLiveError;
use crate::{API_NAME, API_VERSION, ValueExt};

type SendFn = Arc<dyn Fn(Vec<u8>) + Send + Sync>;
/// 首次签发 token 的回调。
pub type TokenCallback = Arc<dyn Fn(&str) + Send + Sync>;
type Waiters = Mutex<HashMap<String, oneshot::Sender<Result<Value, NanaLiveError>>>>;

/// 鉴权时提交给 NanaLive 的插件身份。
///
/// `plugin_id` 请使用自己的反向域名标识，`scopes` 只申请实际用到的权限；
/// 首次申请的 token 需要用户在 NanaLive 插件页批准。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Identity {
    pub plugin_id: String,
    pub plugin_name: String,
    pub plugin_developer: String,
    pub plugin_version: String,
    pub scopes: Vec<String>,
}

impl Identity {
    pub fn to_value(&self) -> Value {
        Value::Map(vec![
            ("pluginID".into(), self.plugin_id.clone().into()),
            ("pluginName".into(), self.plugin_name.clone().into()),
            (
                "pluginDeveloper".into(),
                self.plugin_developer.clone().into(),
            ),
            ("pluginVersion".into(), self.plugin_version.clone().into()),
            (
                "scopes".into(),
                Value::Array(
                    self.scopes
                        .iter()
                        .map(|scope| scope.clone().into())
                        .collect(),
                ),
            ),
        ])
    }
}

/// NanaLive 插件 API 客户端。
///
/// 与传输解耦：构造时注入同步的 `send` 回调负责把编码后的字节写出去，
/// 收到字节后调用 [`NanaLiveClient::receive`] 喂回客户端即可。
pub struct NanaLiveClient {
    send: SendFn,
    identity: Option<Identity>,
    token: Mutex<Option<String>>,
    on_token: Option<TokenCallback>,
    waiters: Waiters,
    sequence: AtomicU64,
}

impl NanaLiveClient {
    pub fn new(
        send: impl Fn(Vec<u8>) + Send + Sync + 'static,
        identity: Option<Identity>,
        token: Option<String>,
        on_token: Option<TokenCallback>,
    ) -> Self {
        Self {
            send: Arc::new(send),
            identity,
            token: Mutex::new(token),
            on_token,
            waiters: Mutex::new(HashMap::new()),
            sequence: AtomicU64::new(0),
        }
    }

    /// 发送一条请求并等待配对的响应。
    pub async fn request(&self, message_type: &str, data: Value) -> Result<Value, NanaLiveError> {
        let sequence = self.sequence.fetch_add(1, Ordering::SeqCst) + 1;
        let request_id = format!("nanalive-{sequence}");
        let (sender, receiver) = oneshot::channel();
        self.waiters
            .lock()
            .unwrap()
            .insert(request_id.clone(), sender);

        let envelope = Value::Map(vec![
            ("apiName".into(), API_NAME.into()),
            ("apiVersion".into(), API_VERSION.into()),
            ("requestID".into(), request_id.into()),
            ("messageType".into(), message_type.into()),
            ("data".into(), data),
        ]);
        let mut bytes = Vec::new();
        rmpv::encode::write_value(&mut bytes, &envelope)
            .map_err(|error| NanaLiveError::Encode(error.to_string()))?;
        (self.send)(bytes);

        match receiver.await {
            Ok(result) => result,
            Err(_) => Err(NanaLiveError::ConnectionClosed(
                "request dropped before response".into(),
            )),
        }
    }

    /// 把一段收到的字节喂回客户端。
    ///
    /// 返回 `Ok(None)` 表示响应已配对给等待中的请求；`Ok(Some(value))`
    /// 表示没有匹配的等待者（服务器主动推送），原样透传给调用方。
    pub fn receive(&self, bytes: &[u8]) -> Result<Option<Value>, NanaLiveError> {
        let response = rmpv::decode::read_value(&mut &bytes[..])
            .map_err(|error| NanaLiveError::Decode(error.to_string()))?;
        Ok(self.receive_value(response))
    }

    /// 已解码响应的配对逻辑，见 [`NanaLiveClient::receive`]。
    pub fn receive_value(&self, response: Value) -> Option<Value> {
        let request_id = response
            .get("requestID")
            .and_then(Value::as_str)
            .map(str::to_string);
        let waiter = request_id.and_then(|id| self.waiters.lock().unwrap().remove(&id));
        let Some(waiter) = waiter else {
            return Some(response);
        };
        if response.get("messageType").and_then(Value::as_str) == Some("APIError") {
            let message = response
                .get("data")
                .and_then(|data| data.get("message"))
                .and_then(Value::as_str)
                .unwrap_or("api_error")
                .to_string();
            let code = response
                .get("data")
                .and_then(|data| data.get("errorCode"))
                .cloned();
            let _ = waiter.send(Err(NanaLiveError::Api { message, code }));
        } else {
            let _ = waiter.send(Ok(response));
        }
        None
    }

    /// 让所有等待中的请求立即失败（连接断开时由会话层调用）。
    ///
    /// 返回清掉的等待者数量。
    pub fn fail_pending(&self, error: NanaLiveError) -> usize {
        let mut waiters = self.waiters.lock().unwrap();
        let count = waiters.len();
        for (_, sender) in waiters.drain() {
            let _ = sender.send(Err(error.clone()));
        }
        count
    }

    /// 两段式鉴权：已有 token 先尝试验证，失败降级为申请新 token。
    pub async fn authenticate(&self) -> Result<Value, NanaLiveError> {
        // 先克隆出锁再进入 await，避免 MutexGuard 跨 await。
        let saved = self.token.lock().unwrap().clone();
        if let Some(token) = saved {
            let payload = Value::Map(vec![("authenticationToken".into(), token.into())]);
            match self.request("AuthenticationRequest", payload).await {
                Ok(response) => return Ok(response),
                Err(_) => {
                    *self.token.lock().unwrap() = None;
                }
            }
        }

        let identity = self
            .identity
            .as_ref()
            .map_or(Value::Nil, Identity::to_value);
        let issued = self
            .request("AuthenticationTokenRequest", identity)
            .await?;
        let token = issued
            .get("data")
            .and_then(|data| data.get("authenticationToken"))
            .and_then(Value::as_str)
            .filter(|token| !token.is_empty())
            .map(str::to_string);
        let Some(token) = token else {
            return Err(NanaLiveError::AuthenticationTokenMissing);
        };
        if let Some(on_token) = &self.on_token {
            on_token(&token);
        }
        *self.token.lock().unwrap() = Some(token.clone());

        let payload = Value::Map(vec![("authenticationToken".into(), token.into())]);
        self.request("AuthenticationRequest", payload).await
    }

    /// `AvailableModelsRequest`。
    pub async fn list_models(&self) -> Result<Value, NanaLiveError> {
        self.request("AvailableModelsRequest", Value::Map(vec![])).await
    }

    /// `MotionListRequest`。
    pub async fn list_motions(&self) -> Result<Value, NanaLiveError> {
        self.request("MotionListRequest", Value::Map(vec![])).await
    }

    /// `ExpressionListRequest`。
    pub async fn list_expressions(&self) -> Result<Value, NanaLiveError> {
        self.request("ExpressionListRequest", Value::Map(vec![]))
            .await
    }

    /// `HotkeyListRequest`。
    pub async fn list_hotkeys(&self) -> Result<Value, NanaLiveError> {
        self.request("HotkeyListRequest", Value::Map(vec![])).await
    }

    /// `ParameterListRequest`。
    pub async fn list_parameters(&self) -> Result<Value, NanaLiveError> {
        self.request("ParameterListRequest", Value::Map(vec![]))
            .await
    }
}
