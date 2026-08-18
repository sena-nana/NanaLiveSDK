import { parseRepo } from "./giscus";

export type Reputation = {
  up: number;
  down: number;
  score: number | null;
};

export type Plugin = {
  name: string;
  repo: string;
  description: string;
  tags?: string[];
  issue?: number;
  license?: string | null;
  archived?: boolean;
  missingTopic?: boolean;
  stars?: number;
  pushedAt?: string | null;
  quality?: number | null;
  qualityTier?: "excellent" | "good" | "ok" | "poor" | string | null;
  reputation?: Reputation | null;
};

export const QUALITY_LABEL: Record<string, string> = {
  excellent: "优秀",
  good: "良好",
  ok: "一般",
  poor: "不足",
};

export function sortPlugins(plugins: Plugin[]) {
  return [...plugins].sort((a, b) => {
    const sa = a.reputation?.score;
    const sb = b.reputation?.score;
    const aHas = sa != null;
    const bHas = sb != null;
    if (aHas && bHas && sa !== sb) return (sb as number) - (sa as number);
    if (aHas !== bHas) return aHas ? -1 : 1;
    const qa = a.quality ?? -1;
    const qb = b.quality ?? -1;
    if (qa !== qb) return qb - qa;
    return a.name.localeCompare(b.name, "en");
  });
}

export function relativeTime(iso?: string | null) {
  if (!iso) return "更新时间未知";
  const delta = Date.now() - Date.parse(iso);
  if (!Number.isFinite(delta)) return "更新时间未知";
  const minutes = Math.floor(delta / 60000);
  if (minutes < 1) return "刚刚更新";
  if (minutes < 60) return `${minutes} 分钟前更新`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前更新`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} 天前更新`;
  const months = Math.floor(days / 30);
  if (months < 12) return `${months} 个月前更新`;
  return `${Math.floor(months / 12)} 年前更新`;
}

export function starSteps(score: number) {
  const half = Math.round(score * 2) / 2;
  return Array.from({ length: 5 }, (_, i) => {
    const n = i + 1;
    if (half >= n) return "full";
    if (half + 0.5 >= n) return "half";
    return "empty";
  });
}

export function repoSlug(plugin: Plugin) {
  return parseRepo(plugin.repo)?.slug || plugin.name;
}

export function detailLink(plugin: Plugin) {
  return `/store/plugin?repo=${encodeURIComponent(repoSlug(plugin))}`;
}
