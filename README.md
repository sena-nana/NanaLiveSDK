# NanaLiveSDK

SDK and Plugins for NanaLive.

本仓库是 NanaLive 的公开 SDK 与官方 Plugin 示例。开发者可在此获取 SDK、对照官方插件示例，并阅读开发文档。

## 仓库结构

```text
NanaLiveSDK/
├── README.md
├── LICENSE
├── SDK/                 # 对外 SDK（协议、绑定、脚手架）
├── Plugins/             # 官方 Plugin 示例，一插件一目录
├── Docs/                # VitePress 文档站（含插件登记表单）
├── registry/            # 第三方插件元数据，不含源码
└── .github/             # Issue 表单与自动开 PR 的 Actions
```

最高层级职责互不重叠：

| 目录 | 职责 |
| --- | --- |
| [SDK](SDK) | SDK 本体：协议定义、语言绑定、插件模板 |
| [Plugins](Plugins) | 官方示例插件（不接受第三方投稿），一插件一个子目录 |
| [Docs](Docs) | 快速开始、插件开发指南、API 索引、登记表单 |
| [registry](registry) | 已审核第三方插件的名称、仓库与简介 |

## 从哪里开始

1. 读 [Docs](Docs) 了解开发流程与文档规划
2. 使用 [SDK](SDK) 作为实现依赖
3. 对照 [Plugins](Plugins) 中的官方示例

SDK 实现、示例插件与文档正文将随协议稳定后陆续补充。

## SDK

[SDK](SDK) 存放对外 SDK。第三方插件应依赖本目录提供的接口，而不是复制官方示例中的实现细节。

## Plugins

[Plugins](Plugins) 只存放官方维护的示例插件。约定 **一个插件一个子目录**。本目录不包含协议规范或 SDK 源码，也不接受第三方插件投稿。

## Docs

[Docs](Docs) 是 VitePress 文档站。`cd Docs && npm install && npm run docs:dev`。部署见 [Docs/guide/deploy.md](Docs/guide/deploy.md)。

## 贡献

欢迎通过 Issue 与 Pull Request 改进 SDK、官方示例与文档。第三方插件请放在自己的仓库并打上 `nanalive` 标签，再通过 [登记插件](Docs/store/publish.md) 提交元数据。

## 许可证

本仓库采用 [MIT License](LICENSE)。
