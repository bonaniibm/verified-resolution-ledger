// Builds deployable Dataverse web resources (full HTML documents) and optional demo pages with embedded sample data.
//   node webresources/build.mjs                       -> webresources/dist/bpc_/vrl/*.html
//   node webresources/build.mjs --demo out/dashboard-demo.json --demo-out artifacts/demo
import { readFileSync, writeFileSync, mkdirSync, readdirSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : null; };

const srcDir = join(here, "src");
const distDir = join(here, "dist", "bpc_", "vrl");
mkdirSync(distDir, { recursive: true });

const fragments = readdirSync(srcDir).filter((f) => f.endsWith(".fragment.html"));
for (const f of fragments) {
  const name = basename(f, ".fragment.html");
  const fragment = readFileSync(join(srcDir, f), "utf8");
  const [head, ...rest] = splitHead(fragment);
  const doc = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
${head}
</head>
<body>
${rest.join("")}
</body>
</html>
`;
  writeFileSync(join(distDir, `${name}.html`), doc);
  console.log(`web resource  -> dist/bpc_/vrl/${name}.html`);
}

const demo = opt("--demo");
if (demo) {
  const outDir = opt("--demo-out") ?? join(here, "..", "artifacts", "demo");
  mkdirSync(outDir, { recursive: true });
  const data = readFileSync(demo, "utf8");
  for (const f of fragments) {
    const name = basename(f, ".fragment.html");
    const fragment = readFileSync(join(srcDir, f), "utf8");
    // Inject sample data right before the page's own script.
    const idx = fragment.lastIndexOf("<script>");
    const page = fragment.slice(0, idx) + `<script>window.VRL_DEMO = ${data.replace(/</g, "\\u003c")};</script>\n` + fragment.slice(idx);
    writeFileSync(join(outDir, `${name}.html`), page);
    console.log(`demo page     -> ${join(outDir, name + ".html")}`);
  }
}

/** Moves <title>, <link> and <style> (the leading block) into <head>; the rest becomes <body>. */
function splitHead(fragment) {
  const marker = fragment.indexOf("</style>");
  if (marker < 0) return ["", fragment];
  const end = marker + "</style>".length;
  return [fragment.slice(0, end), fragment.slice(end)];
}
