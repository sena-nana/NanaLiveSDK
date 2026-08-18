/// <reference path="../env.d.ts" />

export const GISCUS = {
  repo: "sena-nana/NanaLiveSDK",
  repoId: "R_kgDOT8ePXg",
  category: (import.meta.env.VITE_GISCUS_CATEGORY as string | undefined) || "Plugins",
  categoryId:
    (import.meta.env.VITE_GISCUS_CATEGORY_ID as string | undefined) ||
    "DIC_kwDOT8ePXs4DDqHF",
};

export function isGiscusConfigured() {
  return Boolean(GISCUS.repo && GISCUS.repoId && GISCUS.category && GISCUS.categoryId);
}

export function discussionTerm(owner: string, name: string) {
  return `plugin:${owner}/${name}`;
}

export function pluginTerm(repoUrl: string) {
  const parsed = parseRepo(repoUrl);
  return parsed ? discussionTerm(parsed.owner, parsed.name) : "";
}

export function reactionCount(value: unknown) {
  if (typeof value === "number") return value;
  if (value && typeof value === "object" && "count" in value) {
    return Number((value as { count: number }).count || 0);
  }
  return 0;
}

export function votesFromDiscussion(discussion: {
  reactions?: Record<string, unknown>;
}) {
  const reactions = discussion.reactions || {};
  return {
    up: reactionCount(reactions.THUMBS_UP),
    down: reactionCount(reactions.THUMBS_DOWN),
  };
}

export function parseRepo(url: string) {
  const match = String(url || "")
    .trim()
    .replace(/\/$/, "")
    .match(/^https:\/\/github\.com\/([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+)$/);
  if (!match) return null;
  return { owner: match[1], name: match[2], slug: `${match[1]}/${match[2]}` };
}

export function scoreFromVotes(up: number, down: number) {
  const n = Number(up || 0) + Number(down || 0);
  if (n === 0) return null;
  return Math.round((5 * Number(up || 0) * 10) / n) / 10;
}
