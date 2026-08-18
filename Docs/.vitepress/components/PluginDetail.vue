<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import registry from "../../../registry/plugins.json";
import type { Plugin } from "../lib/plugins";
import {
  QUALITY_LABEL,
  relativeTime,
  repoSlug,
  starSteps,
} from "../lib/plugins";
import { discussionTerm, parseRepo, scoreFromVotes } from "../lib/giscus";
import Giscus from "./Giscus.vue";

const slug = ref("");
const reputation = ref<Plugin["reputation"]>(null);

onMounted(() => {
  slug.value = new URLSearchParams(window.location.search).get("repo") || "";
});

const plugin = computed(() => {
  const wanted = slug.value.replace(/\/$/, "").toLowerCase();
  if (!wanted) return null;
  return (registry.plugins as Plugin[]).find((item) => {
    const parsed = parseRepo(item.repo);
    return (
      parsed?.slug.toLowerCase() === wanted ||
      item.name.toLowerCase() === wanted ||
      item.repo.replace(/\/$/, "").toLowerCase() === wanted
    );
  }) || null;
});

const parsed = computed(() => (plugin.value ? parseRepo(plugin.value.repo) : null));
const term = computed(() =>
  parsed.value ? discussionTerm(parsed.value.owner, parsed.value.name) : "",
);
const current = computed(() => {
  if (!plugin.value) return null;
  return {
    ...plugin.value,
    reputation: reputation.value ?? plugin.value.reputation,
  };
});

function onDiscussion(payload: { up: number; down: number }) {
  reputation.value = {
    up: payload.up,
    down: payload.down,
    score: scoreFromVotes(payload.up, payload.down),
  };
}
</script>

<template>
  <p><a href="/store/">← 返回插件市场</a></p>
  <p v-if="slug && !plugin" class="muted">没有找到仓库 {{ slug }} 的登记记录。</p>
  <p v-else-if="!plugin" class="muted">缺少 <code>?repo=owner/name</code> 参数。</p>
  <article v-else-if="current" class="plugin-detail">
    <header>
      <h1>{{ current.name }}</h1>
      <div class="labels">
        <span class="chip">{{ current.license || "未声明" }}</span>
        <span v-if="current.archived" class="chip chip--warn">已归档</span>
        <span v-if="current.missingTopic" class="chip chip--warn">缺少 nanalive</span>
        <span
          v-if="current.quality != null"
          class="chip"
        >
          {{ QUALITY_LABEL[current.qualityTier || "poor"] || "不足" }}
          {{ current.quality }}
        </span>
      </div>
    </header>
    <p class="score">
      <template v-if="current.reputation?.score != null">
        <span class="stars">
          <span
            v-for="(step, i) in starSteps(current.reputation.score)"
            :key="i"
            class="star"
            :class="`star--${step}`"
          >★</span>
        </span>
        <strong>{{ current.reputation.score.toFixed(1) }}</strong>
        <span class="muted">
          赞 {{ current.reputation.up }} · 踩 {{ current.reputation.down }}
        </span>
      </template>
      <span v-else class="muted">暂无评分</span>
    </p>
    <p class="muted">Star {{ current.stars ?? 0 }} · {{ relativeTime(current.pushedAt) }}</p>
    <p>{{ current.description }}</p>
    <p>
      源码：
      <a :href="current.repo" target="_blank" rel="noopener">{{ repoSlug(current) }}</a>
    </p>
    <h2>评论与投票</h2>
    <p class="muted">👍 / 👎 会记入口碑评分（5 × 赞 / (赞 + 踩)），与市场卡片共用同一条 Discussion。</p>
    <Giscus v-if="term" :term="term" @discussion="onDiscussion" />
  </article>
</template>

<style scoped>
.plugin-detail header { margin-bottom: 0.5rem; }
.plugin-detail h1 { margin: 0 0 0.5rem; }
.labels { display: flex; flex-wrap: wrap; gap: 0.35rem; }
.chip {
  display: inline-flex;
  padding: 0.1rem 0.5rem;
  border-radius: 999px;
  font-size: 0.75rem;
  font-weight: 600;
  background: var(--vp-c-default-soft);
  color: var(--vp-c-text-2);
}
.chip--warn {
  background: var(--vp-c-danger-soft);
  color: var(--vp-c-danger-1);
}
.score { display: flex; align-items: center; gap: 0.5rem; }
.star { color: var(--vp-c-gray-3); }
.star--full { color: #eab308; }
.star--half {
  background: linear-gradient(90deg, #eab308 50%, var(--vp-c-gray-3) 50%);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.muted { color: var(--vp-c-text-2); }
</style>
