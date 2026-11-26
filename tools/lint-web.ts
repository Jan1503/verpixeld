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

const files = report.checked_files?.length ?? "?";
if (findings.length || report.errors?.length) {
  console.error(`\n${findings.length} finding(s), ${report.errors?.length ?? 0} parse error(s) in ${files} files`);
  if (stderr.length) console.error(new TextDecoder().decode(stderr));
  Deno.exit(1);
}
console.log(`web UI modules OK (${files} files)`);
