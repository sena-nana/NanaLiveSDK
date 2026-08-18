<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useData } from "vitepress";
import { GISCUS, isGiscusConfigured, votesFromDiscussion } from "../lib/giscus";

const props = defineProps<{
  term: string;
  reactionsOnly?: boolean;
  scan?: boolean;
}>();

const emit = defineEmits<{
  discussion: [payload: { up: number; down: number }];
  missing: [];
}>();

const host = ref<HTMLElement | null>(null);
const { isDark } = useData();

function theme() {
  return isDark.value ? "dark" : "light";
}

function onMessage(event: MessageEvent) {
  if (event.origin !== "https://giscus.app") return;
  const payload = event.data?.giscus;
  if (!payload) return;
  if (payload.discussion) {
    emit("discussion", votesFromDiscussion(payload.discussion));
    return;
  }
  if (payload.error) emit("missing");
}

function mountScript() {
  if (!host.value || !isGiscusConfigured() || !props.term) return;
  host.value.replaceChildren();
  const script = document.createElement("script");
  script.src = "https://giscus.app/client.js";
  script.async = true;
  script.crossOrigin = "anonymous";
  script.setAttribute("data-repo", GISCUS.repo);
  script.setAttribute("data-repo-id", GISCUS.repoId);
  script.setAttribute("data-category", GISCUS.category);
  script.setAttribute("data-category-id", GISCUS.categoryId);
  script.setAttribute("data-mapping", "specific");
  script.setAttribute("data-term", props.term);
  script.setAttribute("data-strict", "1");
  script.setAttribute("data-reactions-enabled", "1");
  script.setAttribute("data-emit-metadata", "1");
  script.setAttribute(
    "data-input-position",
    props.reactionsOnly ? "bottom" : "top",
  );
  script.setAttribute("data-theme", theme());
  script.setAttribute("data-lang", "zh-CN");
  host.value.appendChild(script);
}

function setTheme() {
  const iframe = host.value?.querySelector<HTMLIFrameElement>("iframe.giscus-frame");
  iframe?.contentWindow?.postMessage(
    { giscus: { setConfig: { theme: theme() } } },
    "https://giscus.app",
  );
}

onMounted(() => {
  window.addEventListener("message", onMessage);
  mountScript();
});

onBeforeUnmount(() => {
  window.removeEventListener("message", onMessage);
});

watch(
  () => props.term,
  async () => {
    await nextTick();
    mountScript();
  },
);

watch(isDark, setTheme);
</script>

<template>
  <p v-if="!scan && !isGiscusConfigured()" class="giscus-setup">
    尚未配置 giscus。请确认仓库已启用 Discussions 并安装 giscus App。
  </p>
  <div
    v-else-if="isGiscusConfigured()"
    ref="host"
    class="giscus-host"
    :class="{ 'giscus-host--vote': reactionsOnly && !scan, 'giscus-host--scan': scan }"
  />
</template>

<style scoped>
.giscus-setup {
  margin: 0.75rem 0 0;
  font-size: 0.9rem;
  color: var(--vp-c-text-2);
}
.giscus-host--vote {
  max-height: 14rem;
  overflow: hidden;
}
.giscus-host--scan {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
