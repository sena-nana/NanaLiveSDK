//! 带自动重连、心跳与请求超时的会话层，对应 JS SDK 的 `session.mjs`。
//!
//! 完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活；
//! 断线后挂起中的请求立即失败，并按指数退避（带抖动）自动重连与重新鉴权。
//! 通过 `on_status` 回调观察 [`SessionStatus`] 状态变化。

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError, RwLock};
use std::time::Duration;

use rmpv::Value;
use tokio::sync::watch;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{Identity, NanaLiveClient, TokenCallback};
use crate::connection::{
    connect_with_client, guard, now_millis, ConnectionHandle, ConnectOptions, ErrorCallback,
    UnhandledCallback,
};
use crate::error::NanaLiveError;

/// 会话连接状态，经 `on_status` 回调上报。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SessionStatus {
    /// 正在建立首个连接。
    Connecting,
    /// 已连接且完成鉴权。
    Connected,
    /// 连接断开后正在重连。
    Reconnecting,
    /// 已关闭，或重试耗尽后放弃。
    Disconnected,
}

pub type StatusCallback = Arc<dyn Fn(SessionStatus) + Send + Sync>;

/// [`NanaLiveSession::new`] 的选项，语义与 JS SDK 的 `createNanaLiveSession` 一致。
#[derive(Clone)]
pub struct SessionOptions {
    /// 默认 `127.0.0.1`。
    pub host: String,
    /// 默认 [`crate::DEFAULT_PORT`]（8312）。
    pub port: u16,
    pub identity: Option<Identity>,
    /// 初始 token；重连时由客户端内部复用最新 token。
    pub token: Option<String>,
    /// 首次签发的 token，用于调用方持久化。
    pub on_token: Option<TokenCallback>,
    /// 未配对请求的响应（服务器主动推送）。
    pub on_unhandled: Option<UnhandledCallback>,
    /// 泵任务中的协议/连接错误，以及重连失败的原因。
    pub on_error: Option<ErrorCallback>,
    /// 连接状态变化回调。
    pub on_status: Option<StatusCallback>,
    /// 断线后是否自动重连，默认 `true`。
    pub reconnect: bool,
    /// 重试上限；`None` 表示无限重试。
    pub max_retries: Option<u32>,
    /// 首次重试延迟，默认 500ms，之后指数翻倍。
    pub retry_delay: Duration,
    /// 重试延迟上限，默认 8s。
    pub max_retry_delay: Duration,
    /// 心跳间隔，默认 10s；空闲超过该时长即发送 WebSocket ping。
    pub heartbeat_interval: Duration,
    /// 心跳超时，默认 5s；ping 后仍无任何入站帧即视为死链。
    pub heartbeat_timeout: Duration,
    /// 建链、握手与鉴权的总时长上限，默认 5s；`None` 表示不限制。
    pub connect_timeout: Option<Duration>,
    /// 单请求超时，默认 30s；`None` 表示不限制。
    pub request_timeout: Option<Duration>,
}

impl Default for SessionOptions {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".to_string(),
            port: crate::DEFAULT_PORT,
            identity: None,
            token: None,
            on_token: None,
            on_unhandled: None,
            on_error: None,
            on_status: None,
            reconnect: true,
            max_retries: None,
            retry_delay: Duration::from_millis(500),
            max_retry_delay: Duration::from_millis(8000),
            heartbeat_interval: Duration::from_secs(10),
            heartbeat_timeout: Duration::from_secs(5),
            connect_timeout: Some(Duration::from_secs(5)),
            request_timeout: Some(Duration::from_secs(30)),
        }
    }
}

struct SessionState {
    status: SessionStatus,
    /// 连接代数；`connect()`/`close()` 递增，旧的后台任务据此退出，
    /// 且绝不能改动新一代的共享状态。
    generation: u64,
}

/// NanaLive 插件 API 的弹性会话。
///
/// 请以 `Arc<NanaLiveSession>` 使用（[`NanaLiveSession::connect`] 需要在
/// 后台任务里持有会话引用）。token 与等待队列由内部的
/// [`NanaLiveClient`] 承载，在多次重连之间保持复用。
pub struct NanaLiveSession {
    options: SessionOptions,
    client: Arc<NanaLiveClient>,
    /// 当前连接；`send` 闭包据此把请求写给正在生效的连接。
    ///
    /// 不变式：所有 slot 变更都必须持有 `state` 锁（换代检查与写入
    /// 在同一临界区内），这样旧代任务不可能覆盖新一代的连接。
    slot: Arc<RwLock<Option<Arc<ConnectionHandle>>>>,
    /// 当前连接的断开信号，连同所属代数一起存取。
    disconnected: Mutex<Option<(u64, watch::Sender<u64>)>>,
    state: Mutex<SessionState>,
    closed: AtomicBool,
}

impl NanaLiveSession {
    /// 创建会话；选项非法时返回 [`NanaLiveError::InvalidOption`]。
    pub fn new(options: SessionOptions) -> Result<Self, NanaLiveError> {
        if options.heartbeat_interval.is_zero() {
            return Err(NanaLiveError::InvalidOption(
                "heartbeat_interval must be positive".to_string(),
            ));
        }
        if options.retry_delay.is_zero() || options.max_retry_delay < options.retry_delay {
            return Err(NanaLiveError::InvalidOption(
                "retry_delay must be positive and not exceed max_retry_delay".to_string(),
            ));
        }
        let slot = Arc::new(RwLock::new(None));
        let client_slot = Arc::clone(&slot);
        let client = Arc::new(NanaLiveClient::new(
            move |bytes: Vec<u8>| {
                let handle: Option<Arc<ConnectionHandle>> = client_slot
                    .read()
                    .unwrap_or_else(PoisonError::into_inner)
                    .clone();
                match handle {
                    // 出站通道已关闭（连接刚死）按断线处理，请求立刻失败。
                    Some(handle) if handle.send_raw(Message::Binary(bytes)) => Ok(()),
                    Some(_) => Err(NanaLiveError::ConnectionClosed(
                        "connection_lost".to_string(),
                    )),
                    None => Err(NanaLiveError::NotConnected),
                }
            },
            options.identity.clone(),
            options.token.clone(),
            options.on_token.clone(),
        ));
        Ok(Self {
            options,
            client,
            slot,
            disconnected: Mutex::new(None),
            state: Mutex::new(SessionState {
                status: SessionStatus::Disconnected,
                generation: 0,
            }),
            closed: AtomicBool::new(false),
        })
    }

    /// 底层协议客户端（token 在多次重连之间保持复用）。
    pub fn client(&self) -> Arc<NanaLiveClient> {
        Arc::clone(&self.client)
    }

    pub fn status(&self) -> SessionStatus {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .status
    }

    /// 会话是否处于已连接且连接仍在位的状态。
    pub fn is_connected(&self) -> bool {
        let connected = self
            .state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .status
            == SessionStatus::Connected;
        connected
            && self
                .slot
                .read()
                .unwrap_or_else(PoisonError::into_inner)
                .is_some()
    }

    fn set_status(&self, status: SessionStatus) {
        let callback = {
            let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
            if state.status == status {
                return;
            }
            state.status = status;
            self.options.on_status.clone()
        };
        if let Some(callback) = callback {
            guard(move || callback(status));
        }
    }

    /// 只有 `generation` 仍是当前代时才变更状态（旧 monitor 不得改写新代状态）。
    fn set_status_if_current(&self, generation: u64, status: SessionStatus) {
        if self.is_closed() || self.generation() != generation {
            return;
        }
        self.set_status(status);
    }

    fn report_error(&self, message: &str) {
        if let Some(on_error) = &self.options.on_error {
            guard(|| on_error(message.to_string()));
        }
    }

    fn generation(&self) -> u64 {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .generation
    }

    /// 更新当前连接与断开信号；仅当 `generation` 仍是当前代时生效。
    ///
    /// 换代检查与写入在 `state` 锁的同一临界区内完成，与
    /// `connect()`/`close()` 的换代操作互斥，旧代任务不可能覆盖新连接。
    fn replace_slot(
        &self,
        generation: u64,
        handle: Option<Arc<ConnectionHandle>>,
        signal: Option<watch::Sender<u64>>,
    ) -> Option<Arc<ConnectionHandle>> {
        let state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if state.generation != generation {
            return None;
        }
        let previous;
        {
            let mut slot = self.slot.write().unwrap_or_else(PoisonError::into_inner);
            previous = slot.take();
            *slot = handle;
        }
        *self
            .disconnected
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = signal.map(|sender| (generation, sender));
        previous
    }

    /// 清出指定连接；仅当代数未变且 slot 仍指向它时生效（防误清新连接）。
    fn clear_slot_if_current(&self, generation: u64, handle: &Arc<ConnectionHandle>) {
        let state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if state.generation != generation {
            return;
        }
        let mut slot = self.slot.write().unwrap_or_else(PoisonError::into_inner);
        let is_own = slot
            .as_ref()
            .is_some_and(|current| Arc::ptr_eq(current, handle));
        if is_own {
            *slot = None;
            *self
                .disconnected
                .lock()
                .unwrap_or_else(PoisonError::into_inner) = None;
        }
    }

    fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }

    /// 建立会话（含重试），首个连接完成鉴权后返回。
    ///
    /// 之后的断线由后台任务自动重连；重试耗尽（或 `reconnect = false`
    /// 且连不上）时返回最后的错误。重复调用会重置会话并重新连接。
    pub async fn connect(self: &Arc<Self>) -> Result<(), NanaLiveError> {
        self.closed.store(false, Ordering::SeqCst);
        // 换代并清出旧连接在同一临界区内完成：被取代的 establish/
        // monitor 的换代前检查都过不去，绝不会覆盖新连接。
        let (generation, previous) = {
            let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
            state.generation += 1;
            let generation = state.generation;
            let previous = self
                .slot
                .write()
                .unwrap_or_else(PoisonError::into_inner)
                .take();
            *self
                .disconnected
                .lock()
                .unwrap_or_else(PoisonError::into_inner) = None;
            (generation, previous)
        };
        if let Some(handle) = previous {
            // 重置会话时关闭被替换的旧连接，避免泄漏。
            handle.close().await;
            handle.task.abort();
        }
        // 被替换连接上的挂起请求立即失败，而不是干等请求超时。
        self.client.fail_pending(NanaLiveError::ConnectionClosed(
            "connection_lost".to_string(),
        ));

        let mut attempt: u32 = 0;
        let mut last_error;
        loop {
            self.set_status(if attempt == 0 {
                SessionStatus::Connecting
            } else {
                SessionStatus::Reconnecting
            });
            match self.establish(generation).await {
                Ok((handle, last_activity, signal)) => {
                    self.set_status_if_current(generation, SessionStatus::Connected);
                    self.spawn_monitor(generation, handle, last_activity, signal);
                    return Ok(());
                }
                Err(error) => last_error = error,
            }
            attempt += 1;
            if !self.options.reconnect
                || self.options.max_retries.is_some_and(|max| attempt > max)
            {
                self.set_status(SessionStatus::Disconnected);
                return Err(last_error);
            }
            tokio::time::sleep(self.backoff(attempt)).await;
            if self.is_closed() || self.generation() != generation {
                // 退避期间被 close()/再次 connect() 取代：本轮放弃。
                self.set_status(SessionStatus::Disconnected);
                return Err(NanaLiveError::ConnectionClosed("superseded".to_string()));
            }
        }
    }

    /// 建立一次连接并完成鉴权；成功后接管 slot 与断开信号。
    ///
    /// 建链与鉴权受 `connect_timeout` 约束；期间被 `connect()`/`close()`
    /// 换代时，丢弃半开连接并返回 superseded。
    async fn establish(
        &self,
        generation: u64,
    ) -> Result<(Arc<ConnectionHandle>, Arc<AtomicU64>, watch::Sender<u64>), NanaLiveError> {
        let last_activity = Arc::new(AtomicU64::new(now_millis()));
        let (signal, _) = watch::channel(0u64);
        let activity_for_callback = Arc::clone(&last_activity);
        let signal_for_callback = signal.clone();
        let options = ConnectOptions {
            host: self.options.host.clone(),
            port: self.options.port,
            identity: self.options.identity.clone(),
            token: None, // token 由客户端内部持有
            on_token: self.options.on_token.clone(),
            on_unhandled: self.options.on_unhandled.clone(),
            on_error: self.options.on_error.clone(),
            on_activity: Some(Arc::new(move || {
                activity_for_callback.store(now_millis(), Ordering::SeqCst)
            })),
            on_disconnect: Some(Arc::new(move || {
                let _ = signal_for_callback.send(1);
            })),
        };

        let connect_future = connect_with_client(options, self.client());
        let handle = match self.options.connect_timeout {
            Some(timeout) => match tokio::time::timeout(timeout, connect_future).await {
                Ok(result) => result?,
                Err(_) => return Err(NanaLiveError::ConnectTimeout),
            },
            None => connect_future.await?,
        };
        let handle = Arc::new(handle);
        if self.is_closed() || self.generation() != generation {
            // 已被新的 connect()/close() 取代：丢弃半开连接。
            handle.close().await;
            handle.task.abort();
            return Err(NanaLiveError::ConnectionClosed("superseded".to_string()));
        }
        self.replace_slot(generation, Some(Arc::clone(&handle)), Some(signal.clone()));

        let auth_future = self.client.authenticate();
        let auth_result = match self.options.connect_timeout {
            Some(timeout) => match tokio::time::timeout(timeout, auth_future).await {
                Ok(result) => result,
                Err(_) => Err(NanaLiveError::ConnectTimeout),
            },
            None => auth_future.await,
        };
        if let Err(error) = auth_result {
            self.replace_slot(generation, None, None);
            handle.close().await;
            handle.task.abort();
            return Err(error);
        }
        Ok((handle, last_activity, signal))
    }

    /// 监听当前连接：心跳保活，断开后发起重连循环。
    fn spawn_monitor(
        self: &Arc<Self>,
        generation: u64,
        handle: Arc<ConnectionHandle>,
        last_activity: Arc<AtomicU64>,
        signal: watch::Sender<u64>,
    ) {
        let session = Arc::clone(self);
        tokio::spawn(async move {
            monitor_connection(&session, &handle, &last_activity, &signal, generation).await;

            // 只在仍是当前代时清理共享状态；被 connect()/close() 取代的
            // 旧 monitor 绝不能误清新连接的 slot 或误杀其挂起请求。
            if session.is_closed() || session.generation() != generation {
                return;
            }
            // 连接已断：让挂起中的请求立刻失败，并清出 slot。
            session
                .client
                .fail_pending(NanaLiveError::ConnectionClosed(
                    "connection_lost".to_string(),
                ));
            session.clear_slot_if_current(generation, &handle);

            if !session.options.reconnect {
                session.set_status(SessionStatus::Disconnected);
                return;
            }

            let mut attempt: u32 = 0;
            loop {
                attempt += 1;
                if session.options.max_retries.is_some_and(|max| attempt > max) {
                    session.report_error("reconnect_retries_exhausted");
                    session.set_status_if_current(generation, SessionStatus::Disconnected);
                    return;
                }
                session.set_status_if_current(generation, SessionStatus::Reconnecting);
                tokio::time::sleep(session.backoff(attempt)).await;
                if session.is_closed() || session.generation() != generation {
                    return;
                }
                match session.establish(generation).await {
                    Ok((handle, last_activity, signal)) => {
                        session.set_status_if_current(generation, SessionStatus::Connected);
                        session.spawn_monitor(generation, handle, last_activity, signal);
                        return;
                    }
                    Err(error) => {
                        if session.is_closed() || session.generation() != generation {
                            return;
                        }
                        // 重连失败原因必须可观测，否则只能看到永远在重连。
                        session.report_error(&error.to_string());
                    }
                }
            }
        });
    }

    /// 指数退避 + ±20% 抖动；`attempt` 从 1 计。
    fn backoff(&self, attempt: u32) -> Duration {
        let multiplier = 2u64.saturating_pow(attempt.saturating_sub(1));
        let factor = u32::try_from(multiplier).unwrap_or(u32::MAX);
        let base = self
            .options
            .retry_delay
            .saturating_mul(factor)
            .min(self.options.max_retry_delay);
        let millis = base.as_millis() as u64;
        if millis == 0 {
            return base;
        }
        let jitter_span = (millis / 5).max(1);
        let offset = (now_millis().wrapping_mul(2654435761) % (2 * jitter_span + 1)) as i64
            - jitter_span as i64;
        Duration::from_millis((millis as i64 + offset).max(0) as u64)
    }

    /// 发送一条请求并等待配对的响应；断线时立刻失败。
    ///
    /// 会话未连接时返回 [`NanaLiveError::NotConnected`]；超过
    /// `request_timeout` 返回 [`NanaLiveError::RequestTimeout`]。
    pub async fn request(&self, message_type: &str, data: Value) -> Result<Value, NanaLiveError> {
        if self
            .slot
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .is_none()
        {
            return Err(NanaLiveError::NotConnected);
        }
        let pending = self.client.request(message_type, data);
        match self.options.request_timeout {
            Some(timeout) => match tokio::time::timeout(timeout, pending).await {
                Ok(result) => result,
                Err(_) => Err(NanaLiveError::RequestTimeout),
            },
            None => pending.await,
        }
    }

    /// 停止重连并关闭底层连接；挂起中的请求立即失败。
    pub async fn close(&self) {
        self.closed.store(true, Ordering::SeqCst);
        // 换代与清出连接在同一临界区内，旧的 establish/monitor 不会
        // 再碰共享状态（见 replace_slot/clear_slot_if_current）。
        let (handle, signal) = {
            let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
            state.generation += 1;
            let handle = self
                .slot
                .write()
                .unwrap_or_else(PoisonError::into_inner)
                .take();
            let signal = self
                .disconnected
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .take()
                .map(|(_, sender)| sender);
            (handle, signal)
        };
        if let Some(signal) = signal {
            // 唤醒监听中的 monitor，让它看到 closed 后退出。
            let _ = signal.send(1);
        }
        self.client.fail_pending(NanaLiveError::ConnectionClosed(
            "connection_lost".to_string(),
        ));
        if let Some(handle) = handle {
            handle.close().await;
            handle.task.abort();
        }
        self.set_status(SessionStatus::Disconnected);
    }
}

/// 心跳 + 断开监听，直到连接死亡或会话关闭。
async fn monitor_connection(
    session: &NanaLiveSession,
    handle: &Arc<ConnectionHandle>,
    last_activity: &Arc<AtomicU64>,
    signal: &watch::Sender<u64>,
    generation: u64,
) {
    let mut ticker = tokio::time::interval(session.options.heartbeat_interval);
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut disconnected = signal.subscribe();
    let timeout = (session.options.heartbeat_interval + session.options.heartbeat_timeout)
        .as_millis() as u64;
    loop {
        tokio::select! {
            _ = ticker.tick() => {
                if session.is_closed() || session.generation() != generation {
                    return;
                }
                let idle = now_millis().saturating_sub(last_activity.load(Ordering::SeqCst));
                if idle >= session.options.heartbeat_interval.as_millis() as u64 {
                    handle.ping();
                }
                if idle >= timeout {
                    // 死链：尽力关闭后强制结束泵任务，触发重连。
                    handle.close().await;
                    handle.task.abort();
                    return;
                }
            }
            // watch 保留最后一次变更：即使断开发生在订阅前也能立刻观察到。
            _ = disconnected.changed() => {
                return;
            }
        }
    }
}
