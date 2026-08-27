# NanaLive Stream Deck

官方参考客户端。设备只通过 NanaLive 插件 API 控制模型、动作、按键和参数。

连接 `ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，载荷为 MessagePack 二进制帧。查询和触发也可 `POST /v1`，同样用 MessagePack；申请 token 仍走 WebSocket。

## 目录结构

- `com.nanalive.streamdeck.sdPlugin/`：插件本体。协议客户端来自 [`../../SDK/js`](../../SDK/js)（`@nanalive/sdk`，`file:` 依赖），插件内只保留 Stream Deck 专属的动作定义与授权身份。
- `build.mjs`：组装分发目录的构建脚本。

## 开发

在本目录执行：

```sh
cd com.nanalive.streamdeck.sdPlugin
npm install
npm run build
```

`npm install` 会通过 `file:` 依赖链接到仓库内的 `SDK/js`；`npm run build` 会把插件与 SDK 组装到 `dist/com.nanalive.streamdeck.sdPlugin/`（内嵌 `node_modules`）。

## 安装

1. 在 NanaLive 的插件页开启 API。
2. 把 `dist/com.nanalive.streamdeck.sdPlugin` 复制到 Stream Deck 插件目录：
   - Windows：`%AppData%\Elgato\StreamDeck\Plugins\`
   - macOS：`~/Library/Application Support/com.elgato.StreamDeck/Plugins/`
3. 重启 Stream Deck 软件，添加 NanaLive 动作。
4. 首次按键时，在 NanaLive 插件页允许授权。

其他客户端只要实现同一套 `NanaLiveControlAPI` 即可；协议客户端与连接流程见 [`../../SDK/js`](../../SDK/js) 与 [`../../Docs`](../../Docs)。
