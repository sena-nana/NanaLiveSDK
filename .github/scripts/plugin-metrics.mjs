export function qualityTier(quality) {
  if (quality == null) return null;
  if (quality >= 85) return "excellent";
  if (quality >= 65) return "good";
  if (quality >= 40) return "ok";
  return "poor";
}

export function qualityFromSignals(signals) {
  let score = 0;
  if (signals.readmeLong) score += 25;
  if (signals.hasReleaseOrTag) score += 20;
  if (signals.hasWorkflow) score += 20;
  if (signals.hasSource) score += 15;
  if (signals.hasDescription) score += 10;
  if (signals.hasContributingOrIssueTemplate) score += 10;
  return score;
}

const SOURCE_RE =
  /\.(ts|tsx|js|jsx|mjs|cjs|vue|py|rs|go|cs|java|kt|cpp|cc|c|h|lua|swift)$/i;
const DOC_RE = /(^|\/)(readme|license|changelog|contributing)(\.[^/]+)?$/i;

export function analyzeTree(tree = []) {
  const paths = tree.map((item) => item.path.replaceAll("\\", "/"));
  return {
    hasWorkflow: paths.some((path) =>
      path.startsWith(".github/workflows/") && /\.ya?ml$/i.test(path),
    ),
    hasSource: paths.some(
      (path) => SOURCE_RE.test(path) && !DOC_RE.test(path),
    ),
    hasContributingOrIssueTemplate: paths.some(
      (path) =>
        /(^|\/)contributing(\.[^/]+)?$/i.test(path) ||
        path.startsWith(".github/ISSUE_TEMPLATE/") ||
        /(^|\/)ISSUE_TEMPLATE(\.[^/]+)?$/i.test(path),
    ),
  };
}

export function readmeIsLong(markdown) {
  const text = String(markdown || "")
    .replace(/```[\s\S]*?```/g, " ")
    .replace(/[#>*`[\]()]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  return [...text].length > 200;
}
