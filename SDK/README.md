# SDK

本目录存放 NanaLive 对外提供的 SDK 本体，供第三方开发者集成与编写插件。

## 本目录放什么

- 插件协议与消息定义
- 各语言绑定
- 插件脚手架 / 模板

当前已有以下语言绑定，均为 NanaLive 插件 API 的连接、鉴权与请求客户端，用法见各自 README 与 [`../Docs`](../Docs) 的 API 参考：

- [`js/`](./js)（`@nanalive/sdk`）：需 Node.js ≥ 20
- [`rust/`](./rust)（`nanalive-sdk` crate）：异步运行时为 tokio
- [`python/`](./python)（`nanalive-sdk` 包，import `nanalive_sdk`）：需 Python ≥ 3.10（asyncio）
- [`csharp/`](./csharp)（`Nanalive.Sdk`）：需 .NET 8+

每种语言绑定在自己的子目录内自带各自的工程配置，仓库根不放任何语言的 workspace 设置。

## 本目录不放什么

- 可运行的官方插件示例（见 [`../Plugins`](../Plugins)）
- 面向开发者的使用说明与 API 文档正文（见 [`../Docs`](../Docs)）

## 与其他目录的关系

开发插件时，以本目录的 SDK 为实现依赖，对照 [`../Plugins`](../Plugins) 中的官方示例，并阅读 [`../Docs`](../Docs) 中的开发指南。第三方插件请发布在自己的仓库，并为仓库打上 `nanalive` 标签，不要提交到本仓库。
