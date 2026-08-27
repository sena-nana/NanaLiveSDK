//! NanaLive 插件 API 的 Rust 客户端绑定。
//!
//! 连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议
//! `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、
//! 动作、表情、按键和参数接口。

use rmpv::Value;

pub mod client;
pub mod connection;
pub mod error;
pub mod helpers;

pub use client::{Identity, NanaLiveClient};
pub use connection::{connect, ConnectOptions, ConnectionHandle};
pub use error::NanaLiveError;
pub use helpers::{executable_hotkeys, parameter_value_after_ticks, write_parameter_command};

pub const API_NAME: &str = "NanaLiveControlAPI";
pub const API_VERSION: &str = "2.0";
pub const SUBPROTOCOL: &str = "nanalive-control-v2";
pub const DEFAULT_PORT: u16 = 8312;

/// [`rmpv::Value`] 的便捷访问：按字符串键读取 map 字段。
///
/// 协议里的对象键都是字符串，非 map 值一律返回 `None`。
pub trait ValueExt {
    fn get(&self, key: &str) -> Option<&Value>;
}

impl ValueExt for Value {
    fn get(&self, key: &str) -> Option<&Value> {
        match self {
            Value::Map(entries) => entries
                .iter()
                .find(|(k, _)| k.as_str() == Some(key))
                .map(|(_, v)| v),
            _ => None,
        }
    }
}
