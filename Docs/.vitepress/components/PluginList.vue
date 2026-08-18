<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import registry from "../../../registry/plugins.json";
import type { Plugin } from "../lib/plugins";
import {
  QUALITY_LABEL,
  detailLink,
  relativeTime,
  repoSlug,
  sortPlugins,
  starSteps,
} from "../lib/plugins";
import { pluginTerm, scoreFromVotes } from "../lib/giscus";
import GiscusScan from "./GiscusScan.vue";
import PluginVote from "./PluginVote.vue";

const local = reactive<Record<string, Plugin["reputation"]>>({});
const voting = ref<Plugin | null>(null);
const listed = registry.plugins as Plugin[];

const scanTerms = computed(() =>
  listed.map((plugin) => pluginTerm(plugin.repo)).filter(Boolean),
);

const plugins = computed(() =>
  sortPlugins(
    listed.map((plugin) => ({
      ...plugin,
      reputation: local[plugin.repo] ?? null,
    })),
  ),
);

function onMeta(payload: { term: string; up: number; down: number }) {
  const plugin = listed.find((item) => pluginTerm(item.repo) === payload.term);
  if (!plugin) return;
  local[plugin.repo] = {
    up: payload.up,
    down: payload.down,
    score: scoreFromVotes(payload.up, payload.down),
  };
}

function onVoted(payload: {
  repo: string;
  up: number;
  down: number;
  score: number | null;
}) {
  local[payload.repo] = {
    up: payload.up,
    down: payload.down,
    score: payload.score,
  };
}
</script>

<template>
  <p v-if="!plugins.length" class="store-empty">还没有已合并的第三方插件登记。</p>
  <ul v-else class="plugin-grid">
    <li v-for="plugin in plugins" :key="plugin.repo" class="plugin-card">
      <div class="plugin-card__head">
        <a class="plugin-card__title" :href="detailLink(plugin)">{{ plugin.name }}</a>
        <div class="plugin-card__labels">
          <span class="chip">{{ plugin.license || "未声明" }}</span>
          <span v-if="plugin.archived" class="chip chip--warn">已归档</span>
          <span v-if="plugin.missingTopic" class="chip chip--warn">缺少 nanalive</span>
        </div>
      </div>

      <div class="plugin-card__score">
        <template v-if="plugin.reputation?.score != null">
          <span class="stars" :title="`评分 ${plugin.reputation.score.toFixed(1)}`">
            <span
              v-for="(step, i) in starSteps(plugin.reputation.score)"
              :key="i"
              class="star"
              :class="`star--${step}`"
            >★</span>
          </span>
          <strong>{{ plugin.reputation.score.toFixed(1) }}</strong>
        </template>
        <span v-else class="muted">暂无评分</span>
        <span
          v-if="plugin.quality != null"
          class="chip chip--quality"
          :class="`chip--${plugin.qualityTier || 'poor'}`"
        >
          {{ QUALITY_LABEL[plugin.qualityTier || "poor"] || "不足" }}
          {{ plugin.quality }}
        </span>
      </div>

      <div class="plugin-card__votes">
        <button type="button" @click="voting = plugin">
          赞 {{ plugin.reputation?.up ?? 0 }}
        </button>
        <button type="button" @click="voting = plugin">
          踩 {{ plugin.reputation?.down ?? 0 }}
        </button>
      </div>

      <p class="plugin-card__meta">
        Star {{ plugin.stars ?? 0 }} · {{ relativeTime(plugin.pushedAt) }}
      </p>
      <p class="plugin-card__desc">{{ plugin.description }}</p>
      <p v-if="plugin.tags?.length" class="plugin-card__tags">
        <span v-for="tag in plugin.tags" :key="tag" class="chip chip--tag">{{ tag }}</span>
      </p>
      <p class="plugin-card__links">
        <a :href="plugin.repo" target="_blank" rel="noopener">{{ repoSlug(plugin) }}</a>
      </p>
    </li>
  </ul>
  <PluginVote
    v-if="voting"
    :plugin="voting"
    @close="voting = null"
    @voted="onVoted"
  />
  <GiscusScan v-if="!voting && scanTerms.length" :terms="scanTerms" @meta="onMeta" />
</template>

<style scoped>
.store-empty { color: var(--vp-c-text-2); }
.plugin-grid {
  list-style: none;
  padding: 0;
  margin: 1.25rem 0 0;
  display: grid;
  gap: 1rem;
}
.plugin-card {
  border: 1px solid var(--vp-c-divider);
  border-radius: 12px;
  padding: 1rem 1.1rem;
  background: var(--vp-c-bg-soft);
}
.plugin-card__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem 0.75rem;
}
.plugin-card__title {
  font-weight: 700;
  font-size: 1.05rem;
  color: var(--vp-c-text-1);
  text-decoration: none;
}
.plugin-card__title:hover { color: var(--vp-c-brand-1); }
.plugin-card__labels,
.plugin-card__tags { display: flex; flex-wrap: wrap; gap: 0.35rem; }
.chip {
  display: inline-flex;
  align-items: center;
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
.chip--tag { font-weight: 500; }
.chip--excellent { background: var(--vp-c-brand-soft); color: var(--vp-c-brand-1); }
.chip--good { background: var(--vp-c-success-soft, var(--vp-c-brand-soft)); color: var(--vp-c-brand-1); }
.chip--ok { background: var(--vp-c-warning-soft); color: var(--vp-c-warning-1); }
.chip--poor { background: var(--vp-c-default-soft); }
.plugin-card__score {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 0.65rem;
}
.stars { letter-spacing: 0.05em; }
.star { color: var(--vp-c-gray-3); }
.star--full { color: #eab308; }
.star--half {
  background: linear-gradient(90deg, #eab308 50%, var(--vp-c-gray-3) 50%);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.muted { color: var(--vp-c-text-3); font-size: 0.92rem; }
.plugin-card__votes {
  display: flex;
  gap: 0.5rem;
  margin-top: 0.7rem;
}
.plugin-card__votes button {
  font: inherit;
  cursor: pointer;
  border-radius: 8px;
  border: 1px solid var(--vp-c-divider);
  background: var(--vp-c-bg);
  color: var(--vp-c-text-1);
  padding: 0.28rem 0.7rem;
}
.plugin-card__votes button:hover { border-color: var(--vp-c-brand-1); }
.plugin-card__meta,
.plugin-card__desc,
.plugin-card__links {
  margin: 0.45rem 0 0;
  color: var(--vp-c-text-2);
  font-size: 0.92rem;
}
.plugin-card__desc { color: var(--vp-c-text-1); }
.plugin-card__tags { margin: 0.45rem 0 0; }
</style>
