import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

for (const file of ["../auth.mjs", "../server.mjs", "../public/app.js", "build.mjs"]) {
  execFileSync(process.execPath, ["--check", fileURLToPath(new URL(file, import.meta.url))], {
    stdio: "inherit",
  });
}
