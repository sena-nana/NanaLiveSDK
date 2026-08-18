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

## 评论与投票 {#giscus}

插件市场的赞/踩和评论走 [giscus](https://giscus.app/)（GitHub Discussions），不自建后端。仓库已启用 Discussions、分类 `Plugins`，并安装了 giscus App。`repo-id` 与 `category-id` 已写在文档站代码里，一般不必再配环境变量。

若要覆盖（例如换分类），可在 Cloudflare Pages 构建环境里设置：

| 变量 | 默认 |
| --- | --- |
| `VITE_GISCUS_CATEGORY` | `Plugins` |
| `VITE_GISCUS_CATEGORY_ID` | `DIC_kwDOT8ePXs4DDqHF` |

口碑评分公式：`5 × 赞 / (赞 + 踩)`。星星由页面上的 giscus 元数据推送在本地计算；每日 Action 只刷新 license、归档、GitHub Star、更新时间和优秀程度。
