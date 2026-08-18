# 已登记插件

列表来自 `registry/plugins.json`，只含已合并的元数据。源码在作者自己的仓库。

<script setup>
import registry from "../../registry/plugins.json";
</script>

<ul v-if="registry.plugins.length">
  <li v-for="plugin in registry.plugins" :key="plugin.repo">
    <a :href="plugin.repo">{{ plugin.name }}</a>
    — {{ plugin.description }}
  </li>
</ul>
<p v-else>还没有已合并的第三方插件登记。</p>

要登记新插件，请打开 [登记插件](/store/publish)。
