# 部署文档站

VitePress 站点部署到 **Cloudflare Pages**，不要用 GitHub Pages。

本地：`cd Docs && npm install && npm run docs:build`，产物在 `.vitepress/dist`。

Cloudflare Pages 连接本仓库后：

| 项 | 值 |
| --- | --- |
| Root directory | `Docs` |
| Build command | `npm run docs:build` |
| Build output directory | `.vitepress/dist` |
| Node.js | 20 |

未买域名时用 `*.pages.dev`。绑定自定义域名时保持橙云代理。大陆访问无备案时仍走境外节点。
