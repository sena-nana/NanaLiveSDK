import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { githubJson, parseRepo } from "./github-api.mjs";
import {
  analyzeTree,
  qualityFromSignals,
  qualityTier,
  readmeIsLong,
} from "./plugin-metrics.mjs";

const registryPath = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../registry/plugins.json",
);

function decodeReadme(payload) {
  if (!payload?.content) return "";
  return Buffer.from(payload.content, "base64").toString("utf8");
}

async function fetchQualitySignals(owner, name, remote) {
  const [readme, treePayload, releases, tags] = await Promise.all([
    githubJson(`/repos/${owner}/${name}/readme`),
    remote.default_branch
      ? githubJson(
          `/repos/${owner}/${name}/git/trees/${encodeURIComponent(remote.default_branch)}?recursive=1`,
        )
      : null,
    githubJson(`/repos/${owner}/${name}/releases?per_page=1`),
    githubJson(`/repos/${owner}/${name}/tags?per_page=1`),
  ]);
  const tree = analyzeTree(treePayload?.tree || []);
  return {
    readmeLong: readmeIsLong(decodeReadme(readme)),
    hasReleaseOrTag: Boolean(releases?.length || tags?.length),
    hasWorkflow: tree.hasWorkflow,
    hasSource: tree.hasSource,
    hasDescription: Boolean(String(remote.description || "").trim()),
    hasContributingOrIssueTemplate: tree.hasContributingOrIssueTemplate,
  };
}

async function enrichPlugin(plugin) {
  const parsed = parseRepo(plugin.repo);
  if (!parsed) throw new Error(`无效仓库地址：${plugin.repo}`);
  const remote = await githubJson(`/repos/${parsed.owner}/${parsed.name}`);
  const { reputation: _ignored, ...rest } = plugin;
  if (!remote) {
    return { ...rest, missingTopic: true, archived: Boolean(plugin.archived) };
  }
  const topics = (remote.topics || []).map((topic) => topic.toLowerCase());
  const signals = await fetchQualitySignals(parsed.owner, parsed.name, remote);
  const quality = qualityFromSignals(signals);
  const license =
    remote.license?.spdx_id && remote.license.spdx_id !== "NOASSERTION"
      ? remote.license.spdx_id
      : null;
  return {
    ...rest,
    license,
    archived: Boolean(remote.archived),
    missingTopic: !topics.includes("nanalive"),
    stars: Number(remote.stargazers_count || 0),
    pushedAt: remote.pushed_at || null,
    quality,
    qualityTier: qualityTier(quality),
  };
}

const registry = JSON.parse(fs.readFileSync(registryPath, "utf8"));
registry.plugins ??= [];
const next = [];
for (const plugin of registry.plugins) {
  next.push(await enrichPlugin(plugin));
}
registry.plugins = next;
fs.writeFileSync(registryPath, `${JSON.stringify(registry, null, 2)}\n`);
console.log(`Updated metrics for ${next.length} plugin(s)`);
