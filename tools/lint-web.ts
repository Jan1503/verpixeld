// Checks the web UI modules (verpixeld/wwwroot/js) for mistakes a browser only reports at run time:
// a name used but neither declared nor imported (no-undef), assignments to imports, redeclarations,
// duplicate keys, unreachable code. Deno knows most browser APIs; the few it does not are listed below.
//
//   deno run --allow-run --allow-read tools/lint-web.ts
//
// Exit code 1 and a list of findings when something is wrong.

const BROWSER_GLOBALS = new Set([
  "document",
  "CSS",
  "Image",
  "requestAnimationFrame",
  // set by ui/toast.js on window and used bare by a few modules
  "toast",
]);

const root = new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const config = `${root}tools/lint-web.json`;
const target = `${root}verpixeld/wwwroot/js`;

const { stdout, stderr } = await new Deno.Command(Deno.execPath(), {
  args: ["lint", "--config", config, "--json", target],
  stdout: "piped",
  stderr: "piped",
}).output();

const report = JSON.parse(new TextDecoder().decode(stdout));
const findings = report.diagnostics.filter((d: { code: string; message: string }) => {
  if (d.code !== "no-undef") return true;
  const name = /(\w+) is not defined/.exec(d.message)?.[1];
  return !name || !BROWSER_GLOBALS.has(name);
});

for (const e of report.errors ?? []) console.error(`parse error: ${e.file_path}: ${e.message}`);
for (const d of findings) {
  const file = d.filename.replace(/^file:\/\/\/?/, "").split("/wwwroot/").pop();
  console.error(`${file}:${d.range.start.line}:${d.range.start.col + 1}  ${d.code}  ${d.message}`);
}

// No inline event handlers (onclick="…", onerror="…"): controls carry data-* markers wired in JS.
const inline: string[] = [];
const scan = async (path: string) => {
  const text = await Deno.readTextFile(path);
  text.split("\n").forEach((line, i) => {
    const m = /\son[a-z]+\s*=\s*["'`$]/.exec(line);
    if (m) inline.push(`${path.split("/wwwroot/").pop()}:${i + 1}  inline handler  ${line.trim().slice(0, 90)}`);
  });
};
await scan(`${root}verpixeld/wwwroot/index.html`);
const walk = async (dir: string): Promise<void> => {
  for await (const e of Deno.readDir(dir)) {
    const p = `${dir}/${e.name}`;
    if (e.isDirectory) await walk(p);
    else if (p.endsWith(".js")) await scan(p);
  }
};
await walk(target);
for (const line of inline) console.error(line);
findings.push(...inline.map((message) => ({ code: "inline-handler", message })));

// A module function named like a runtime global (reportError, structuredClone, …) hides a missing
// import from no-undef: the other module silently calls the built-in instead. Forbid such names.
const runtimeGlobals = new Set(Object.getOwnPropertyNames(globalThis));
const shadowing: string[] = [];
const declarations = /^(?:export\s+)?(?:async\s+)?(?:function\s*\*?\s*|const\s+|let\s+|var\s+|class\s+)(\w+)/gm;
const collect = async (dir: string): Promise<void> => {
  for await (const e of Deno.readDir(dir)) {
    const p = `${dir}/${e.name}`;
    if (e.isDirectory) { await collect(p); continue; }
    if (!p.endsWith(".js")) continue;
    for (const m of (await Deno.readTextFile(p)).matchAll(declarations)) {
      if (runtimeGlobals.has(m[1])) shadowing.push(`${p.split("/wwwroot/").pop()}  shadows-global  '${m[1]}' is a browser/runtime global; pick another name`);
    }
  }
};
await collect(target);
for (const line of shadowing) console.error(line);
findings.push(...shadowing.map((message) => ({ code: "shadows-global", message })));

const files = report.checked_files?.length ?? "?";
if (findings.length || report.errors?.length) {
  console.error(`\n${findings.length} finding(s), ${report.errors?.length ?? 0} parse error(s) in ${files} files`);
  if (stderr.length) console.error(new TextDecoder().decode(stderr));
  Deno.exit(1);
}
console.log(`web UI modules OK (${files} files)`);
