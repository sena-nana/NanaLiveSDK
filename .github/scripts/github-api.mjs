const API = "https://api.github.com";
const REPO_RE =
  /^https:\/\/github\.com\/([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+)\/?$/;

export function parseRepo(url) {
  const match = String(url || "")
    .trim()
    .replace(/\/$/, "")
    .match(REPO_RE);
  if (!match) return null;
  return { owner: match[1], name: match[2], slug: `${match[1]}/${match[2]}` };
}

function token() {
  return process.env.GITHUB_TOKEN || process.env.GH_TOKEN || "";
}

export async function githubJson(path) {
  const headers = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "nanalive-plugin-registry",
  };
  const auth = token();
  if (auth) headers.Authorization = `Bearer ${auth}`;
  const res = await fetch(`${API}${path}`, { headers });
  if (res.status === 404) return null;
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`GitHub ${res.status} ${path}: ${text.slice(0, 400)}`);
  }
  return res.json();
}
