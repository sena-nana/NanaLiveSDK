# 登记插件

填写后会跳转到 GitHub Issue 预填页，确认后点 `Submit new issue`。维护者合并 PR 后会出现在 [已登记插件](/store/)。

插件源码放在你自己的公开仓库，并先打上 `nanalive` topic（GitHub 仓库页右栏 Topics）。登记 Action 会校验，没有该标签则失败、不开 PR。不要把源码提交到 `Plugins/`。需要登录 GitHub。

<script setup>
const REPO = "sena-nana/NanaLiveSDK";
const REPO_RE = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/?$/;

function submit(event) {
  const data = Object.fromEntries(new FormData(event.target));
  const name = String(data.name || "").trim();
  const repo = String(data.repo || "").trim().replace(/\/$/, "");
  const description = String(data.description || "").trim();
  const tags = String(data.tags || "").trim();
  if (!REPO_RE.test(repo)) {
    event.target.querySelector("[data-error]").textContent =
      "仓库地址请使用 https://github.com/owner/repo";
    return;
  }
  const params = new URLSearchParams({
    template: "plugin_publish.yml",
    title: `Plugin: ${name}`,
    name,
    repo,
    description,
    tags,
  });
  window.open(
    `https://github.com/${REPO}/issues/new?${params}`,
    "_blank",
    "noopener,noreferrer",
  );
}
</script>

<form class="nana-form" @submit.prevent="submit">
  <label>
    插件名称
    <input name="name" maxlength="50" required placeholder="hello-world" />
  </label>
  <label>
    仓库地址
    <input name="repo" type="url" required placeholder="https://github.com/owner/nanalive-plugin-hello" />
  </label>
  <label>
    简介
    <textarea name="description" maxlength="200" required placeholder="一两句话说明插件做什么" />
  </label>
  <label>
    标签（可选，逗号分隔）
    <input name="tags" placeholder="overlay, chat" />
  </label>
  <p data-error class="nana-error"></p>
  <button type="submit">前往 GitHub 提交</button>
</form>

<style>
.nana-form { display: grid; gap: 1rem; max-width: 36rem; margin-top: 1rem; }
.nana-form label { display: grid; gap: 0.35rem; font-weight: 600; }
.nana-form input, .nana-form textarea {
  font: inherit; font-weight: 400; padding: 0.5rem 0.75rem;
  border: 1px solid var(--vp-c-divider); border-radius: 8px;
  background: var(--vp-c-bg-alt); color: var(--vp-c-text-1);
}
.nana-form textarea { min-height: 6rem; resize: vertical; }
.nana-form button {
  justify-self: start; border: 0; border-radius: 8px; padding: 0.55rem 1rem;
  font: inherit; font-weight: 600; color: #fff; background: var(--vp-c-brand-1); cursor: pointer;
}
.nana-error { color: var(--vp-c-danger-1); min-height: 1.2em; }
</style>
