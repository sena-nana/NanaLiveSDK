# 插件开发

插件应依赖 `SDK/` 提供的接口，不要复制官方示例里的实现细节。`Plugins/` 只放官方示例，一插件一目录，不接受第三方源码。

JavaScript 插件从 [`SDK/js`](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/js)（`@nanalive/sdk`）开始：协议常量、`createNanaLiveClient` 与 Node 环境的 WebSocket 助手都在其中，用法见 [API 参考](/api/)。官方参考实现在 [`Plugins/streamdeck/`](https://github.com/sena-nana/NanaLiveSDK/tree/main/Plugins/streamdeck)，可作为目录组织与构建方式的对照。

发布第三方插件：源码放在自己的公开仓库并打上 `nanalive` topic，不要提交到本仓库。
