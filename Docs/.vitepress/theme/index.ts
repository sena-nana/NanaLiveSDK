import DefaultTheme from "vitepress/theme";
import type { Theme } from "vitepress";
import PluginList from "../components/PluginList.vue";
import PluginDetail from "../components/PluginDetail.vue";
import "./custom.css";

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component("PluginList", PluginList);
    app.component("PluginDetail", PluginDetail);
  },
} satisfies Theme;
