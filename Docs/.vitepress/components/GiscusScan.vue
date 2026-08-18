<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import Giscus from "./Giscus.vue";

const props = defineProps<{
  terms: string[];
}>();

const emit = defineEmits<{
  meta: [payload: { term: string; up: number; down: number }];
}>();

const index = ref(0);
const term = computed(() => props.terms[index.value] || "");
const seen = ref("");
let timer = 0;

function advance() {
  window.clearTimeout(timer);
  if (index.value < props.terms.length) index.value += 1;
}

function armTimeout() {
  window.clearTimeout(timer);
  seen.value = "";
  if (!term.value) return;
  timer = window.setTimeout(advance, 5000);
}

watch(term, armTimeout, { immediate: true });
onUnmounted(() => window.clearTimeout(timer));

function onDiscussion(payload: { up: number; down: number }) {
  if (!term.value || seen.value === term.value) return;
  seen.value = term.value;
  emit("meta", { term: term.value, ...payload });
  advance();
}
</script>

<template>
  <Giscus
    v-if="term"
    :term="term"
    reactions-only
    scan
    @discussion="onDiscussion"
    @missing="advance"
  />
</template>
