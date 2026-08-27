import { cpSync, mkdirSync, rmSync } from "node:fs";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, "com.nanalive.streamdeck.sdPlugin");
const dist = join(here, "dist", "com.nanalive.streamdeck.sdPlugin");
const sdk = resolve(here, "com.nanalive.streamdeck.sdPlugin/node_modules/@nanalive/sdk");
const nodeModules = join(source, "node_modules");

rmSync(dist, { recursive: true, force: true });
mkdirSync(dirname(dist), { recursive: true });
cpSync(source, dist, {
  recursive: true,
  filter: (path) =>
    path !== nodeModules &&
    !path.startsWith(nodeModules + sep) &&
    path !== join(source, "package-lock.json"),
});
mkdirSync(join(dist, "node_modules/@nanalive"), { recursive: true });
cpSync(sdk, join(dist, "node_modules/@nanalive/sdk"), {
  recursive: true,
  dereference: true,
});
console.log(`built ${dist}`);
