import { defineConfig } from "vitepress";

const githubRepo = "https://github.com/sena-nana/NanaLiveSDK";

export default defineConfig({
  lang: "zh-CN",
  title: "NanaLive SDK",
  description: "NanaLive SDK 与插件开发文档",
  srcExclude: ["README.md"],
  themeConfig: {
    nav: [
      { text: "指南", link: "/guide/" },
      { text: "API", link: "/api/" },
    ],
    sidebar: {
      "/guide/": [
        {
          text: "指南",
          items: [
            { text: "快速开始", link: "/guide/" },
            { text: "插件开发", link: "/guide/plugin" },
            { text: "部署文档站", link: "/guide/deploy" },
          ],
        },
      ],
    },
    socialLinks: [{ icon: "github", link: githubRepo }],
    search: { provider: "local" },
  },
});
