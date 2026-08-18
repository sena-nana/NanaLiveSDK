<script setup lang="ts">
import { computed, onMounted, onUnmounted } from "vue";
import type { Plugin } from "../lib/plugins";
import { detailLink } from "../lib/plugins";
import { discussionTerm, parseRepo, scoreFromVotes } from "../lib/giscus";
import Giscus from "./Giscus.vue";

const props = defineProps<{
  plugin: Plugin;
}>();

const emit = defineEmits<{
  close: [];
  voted: [payload: { repo: string; up: number; down: number; score: number | null }];
}>();

const parsed = computed(() => parseRepo(props.plugin.repo));
const term = computed(() =>
  parsed.value ? discussionTerm(parsed.value.owner, parsed.value.name) : "",
);

function onKey(event: KeyboardEvent) {
  if (event.key === "Escape") emit("close");
}

onMounted(() => document.addEventListener("keydown", onKey));
onUnmounted(() => document.removeEventListener("keydown", onKey));

function onDiscussion(payload: { up: number; down: number }) {
  emit("voted", {
    repo: props.plugin.repo,
    up: payload.up,
    down: payload.down,
    score: scoreFromVotes(payload.up, payload.down),
  });
}
</script>

<template>
  <div class="vote-overlay" @click.self="emit('close')">
    <div class="vote-panel" role="dialog" aria-modal="true" :aria-label="`为 ${plugin.name} 投票`">
      <header>
        <h3>为 {{ plugin.name }} 投票</h3>
        <button type="button" class="vote-close" @click="emit('close')">关闭</button>
      </header>
      <p>请在下方点 👍 表示赞、👎 表示踩（需 GitHub 登录）。同一账号只能保留一种。</p>
      <Giscus v-if="term" :term="term" reactions-only @discussion="onDiscussion" />
      <p class="vote-more">
        <a :href="detailLink(plugin)">去详情页写评论</a>
      </p>
    </div>
  </div>
</template>

<style scoped>
.vote-overlay {
  position: fixed;
  inset: 0;
  z-index: 40;
  display: grid;
  place-items: center;
  padding: 1rem;
  background: rgb(0 0 0 / 45%);
}
.vote-panel {
  width: min(36rem, 100%);
  max-height: min(90vh, 36rem);
  overflow: auto;
  padding: 1rem 1.1rem 1.2rem;
  border-radius: 12px;
  background: var(--vp-c-bg);
  border: 1px solid var(--vp-c-divider);
  box-shadow: var(--vp-shadow-3);
}
.vote-panel header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}
.vote-panel h3 {
  margin: 0;
  font-size: 1.05rem;
}
.vote-panel p {
  margin: 0.6rem 0 0;
  color: var(--vp-c-text-2);
  font-size: 0.92rem;
}
.vote-close {
  border: 0;
  background: transparent;
  color: var(--vp-c-text-2);
  cursor: pointer;
  font: inherit;
}
.vote-more {
  margin-top: 0.75rem;
}
</style>
