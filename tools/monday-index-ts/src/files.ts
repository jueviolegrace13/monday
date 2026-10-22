import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";

const SOURCE = /\.(ts|tsx|mts|cts|js|jsx|mjs|cjs)$/;
const SKIPPED_DIRS = new Set(["node_modules", ".git", ".monday", ".next", ".turbo", ".vercel", "dist", "build", "out", "coverage", "bin", "obj"]);

/** Repo-relative path with forward slashes. */
export function rel(root: string, file: string): string {
  return path.relative(root, file).split(path.sep).join("/");
}

/** Folders listed in .monday/ignore (one per line, # for comments) are left out of the graph. */
function ignored(root: string): string[] {
  const file = path.join(root, ".monday", "ignore");
  if (!existsSync(file)) return [];
  return readFileSync(file, "utf8")
    .split(/\r?\n/)
    .map((l) => l.trim().replace(/\/+$/, ""))
    .filter((l) => l.length > 0 && !l.startsWith("#"));
}

/** Files git tracks or would track (respects .gitignore), or null outside a git repo. */
function gitFiles(root: string): string[] | null {
  try {
    const out = execFileSync("git", ["-C", root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"], {
      encoding: "utf8",
      maxBuffer: 256 * 1024 * 1024,
      stdio: ["ignore", "pipe", "ignore"],
    });
    return out.split("\0").filter((f) => f.length > 0);
  } catch {
    return null;
  }
}

function walk(root: string, dir = root, found: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIPPED_DIRS.has(entry.name)) walk(root, path.join(dir, entry.name), found);
    } else if (entry.isFile()) {
      found.push(rel(root, path.join(dir, entry.name)));
    }
  }
  return found;
}

/**
 * The TypeScript and JavaScript source files of a repo, repo-relative and sorted. Declaration files,
 * generated build output and anything under .monday/ignore are left out. Only source files are ever read:
 * .env files, settings and credentials never are.
 */
export function sourceFiles(root: string): string[] {
  const prefixes = ignored(root);
  const all = gitFiles(root) ?? walk(root);
  return all
    .filter((f) => SOURCE.test(f) && !/\.d\.[mc]?ts$/.test(f))
    .filter((f) => !f.split("/").some((part) => SKIPPED_DIRS.has(part)))
    .filter((f) => !prefixes.some((p) => f.startsWith(`${p}/`)))
    .filter((f) => existsSync(path.join(root, f)))
    .sort();
}

/** Every package.json folder in the repo (repo-relative, "" for the root), deepest first. */
export function packageDirs(root: string, files: string[]): string[] {
  const dirs = new Set<string>();
  for (const file of files) {
    let dir = path.posix.dirname(file);
    while (dir !== "." && dir !== "") {
      dirs.add(dir);
      dir = path.posix.dirname(dir);
    }
  }
  return [...dirs]
    .filter((d) => existsSync(path.join(root, d, "package.json")))
    .concat(existsSync(path.join(root, "package.json")) ? [""] : [])
    .sort((a, b) => b.split("/").length - a.split("/").length || (a < b ? -1 : 1));
}

/** tsconfig.json files in the repo outside skipped folders, shallowest first. */
export function tsconfigs(root: string): string[] {
  const found: string[] = [];
  const visit = (dir: string, depth: number): void => {
    if (depth > 6) return;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory() && !SKIPPED_DIRS.has(entry.name) && !entry.name.startsWith(".")) visit(path.join(dir, entry.name), depth + 1);
      else if (entry.isFile() && entry.name === "tsconfig.json") found.push(path.join(dir, entry.name));
    }
  };
  visit(root, 0);
  return found.sort((a, b) => a.split(path.sep).length - b.split(path.sep).length || (a < b ? -1 : 1));
}

export function headCommit(root: string): string {
  try {
    const sha = execFileSync("git", ["-C", root, "rev-parse", "--short=12", "HEAD"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
    return sha.length > 0 ? sha : "uncommitted";
  } catch {
    return "uncommitted";
  }
}
