# 插件开发

插件应依赖 `SDK/` 提供的接口，不要复制官方示例里的实现细节。`Plugins/` 只放官方示例，一插件一目录，不接受第三方源码。

发布第三方插件：源码放在自己的公开仓库并打上 `nanalive` 标签，然后到 [登记插件](/store/publish) 填写表单。GitHub Actions 会根据 Issue 更新 `registry/plugins.json` 并开 PR。
