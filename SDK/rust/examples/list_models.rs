//! 连接 NanaLive，鉴权后打印模型目录。
//!
//! 运行：`cargo run --example list_models`（需要 NanaLive 正在运行；
//! 没有服务端时会报告连接错误）。

use std::sync::Arc;

use nanalive_sdk::{connect, ConnectOptions, Identity, DEFAULT_PORT};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let options = ConnectOptions {
        host: "127.0.0.1".into(),
        port: DEFAULT_PORT,
        identity: Some(Identity {
            plugin_id: "dev.example.nanalive-rust-demo".into(),
            plugin_name: "NanaLive Rust Demo".into(),
            plugin_developer: "Example".into(),
            plugin_version: "0.1.0".into(),
            scopes: vec!["model.read".into()],
        }),
        on_token: Some(Arc::new(|token| {
            println!("首次签发的 token（请持久化，下次直接传入）: {token}");
        })),
        ..ConnectOptions::new()
    };

    let handle = connect(options).await?;
    handle.client.authenticate().await?;

    let models = handle.client.list_models().await?;
    println!("模型目录: {models:#?}");

    handle.close().await;
    handle.task.await?;
    Ok(())
}
