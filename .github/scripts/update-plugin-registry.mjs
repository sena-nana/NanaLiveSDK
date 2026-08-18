import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { githubJson, parseRepo } from "./github-api.mjs";

const REPO_RE =
  /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/?$/;
const FIELDS = {
  插件名称: "name",
  仓库地址: "repo",
  简介: "description",
  标签: "tags",
};

function parseIssueBody(body) {
  const fields = {};
  const text = String(body || "")
    .replace(/^\uFEFF/, "")
    .replace(/\r\n/g, "\n");
  for (const chunk of text.split(/^### /m).slice(1)) {
    const i = chunk.indexOf("\n");
    const heading = (i === -1 ? chunk : chunk.slice(0, i)).trim();
    const id = FIELDS[heading];
    if (!id) continue;
    const value = (i === -1 ? "" : chunk.slice(i + 1)).trim();
    fields[id] = /^(_no response_|none|n\/a)$/i.test(value) ? "" : value;
  }
  return fields;
}

function readIssue() {
  if (process.env.GITHUB_EVENT_PATH) {
    const event = JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, "utf8"));
    return event.issue || {};
  }
  return {
    body: fs.readFileSync(process.argv[2] || "issue-body.md", "utf8"),
    number: Number(process.env.ISSUE_NUMBER || 0),
  };
}

function out(name, value) {
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
  }
}

const issue = readIssue();
const fields = parseIssueBody(issue.body);
const name = String(fields.name || "").trim();
const repo = String(fields.repo || "").trim().replace(/\/$/, "");
const description = String(fields.description || "").trim();
const tags = String(fields.tags || "")
  .split(/[,，]/)
  .map((tag) => tag.trim())
  .filter(Boolean)
  .slice(0, 5);

if (!name || name.length > 50) throw new Error("插件名称无效或过长");
if (!REPO_RE.test(repo)) throw new Error("仓库地址必须是 https://github.com/owner/repo");
if (!description || description.length > 200) throw new Error("简介无效或过长");

const parsed = parseRepo(repo);
if (!parsed) throw new Error("仓库地址必须是 https://github.com/owner/repo");
const remote = await githubJson(`/repos/${parsed.owner}/${parsed.name}`);
if (!remote) throw new Error(`仓库不存在或未公开：${repo}`);
const topics = (remote.topics || []).map((topic) => topic.toLowerCase());
if (!topics.includes("nanalive")) {
  throw new Error(
    `仓库缺少 GitHub topic「nanalive」。请在 ${parsed.slug} 的 Settings → Topics 添加后再提交。`,
  );
}

const registryPath = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../registry/plugins.json",
);
const registry = JSON.parse(fs.readFileSync(registryPath, "utf8"));
registry.plugins ??= [];
if (registry.plugins.some((plugin) => plugin.repo.replace(/\/$/, "") === repo)) {
  throw new Error(`仓库已登记：${repo}`);
}

registry.plugins.push({
  name,
  repo,
  description,
  tags,
  issue: issue.number,
  license: remote.license?.spdx_id && remote.license.spdx_id !== "NOASSERTION"
    ? remote.license.spdx_id
    : null,
  archived: Boolean(remote.archived),
  missingTopic: false,
  stars: Number(remote.stargazers_count || 0),
  pushedAt: remote.pushed_at || null,
  quality: null,
  qualityTier: null,
});
registry.plugins.sort((a, b) => a.name.localeCompare(b.name, "en"));
fs.writeFileSync(registryPath, `${JSON.stringify(registry, null, 2)}\n`);
out("name", name);
out("repo", repo);
console.log(`Added ${name} -> ${repo}`);
