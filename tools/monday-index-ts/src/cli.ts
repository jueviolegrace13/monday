#!/usr/bin/env node
import { existsSync, mkdirSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { headCommit } from "./files.ts";
import { RepoIndexer } from "./indexer.ts";

// monday-index-ts: reads a repo's TypeScript and JavaScript (Next.js aware) and writes Monday's code graph.
//   monday-index-ts --root <repo> [--name <app name>] [--out .monday/graph.json] [--commit <sha>] [--upload <monday url>]
// --upload sends the graph to Monday with the key in the MONDAY_KEY environment variable (never a flag, so it stays out of logs).
// The repo is only read: nothing in it is moved or changed, apart from the --out file.

const USAGE = "usage: monday-index-ts --root <repo> [--name <app>] [--out <file>] [--commit <sha>] [--upload <url>]";

function value(args: string[], flag: string): string | undefined {
  const i = args.indexOf(flag);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
}

async function main(args: string[]): Promise<number> {
  const rootArg = value(args, "--root");
  if (!rootArg || !existsSync(rootArg) || !statSync(rootArg).isDirectory()) {
    console.error(USAGE);
    return 2;
  }

  const root = path.resolve(rootArg);
  const name = value(args, "--name") ?? path.basename(root);
  const out = path.resolve(value(args, "--out") ?? path.join(root, ".monday", "graph.json"));
  const commit = value(args, "--commit") ?? headCommit(root);
  const upload = value(args, "--upload");

  const graph = new RepoIndexer(root, name).index(commit);
  mkdirSync(path.dirname(out), { recursive: true });
  writeFileSync(out, `${JSON.stringify(graph, null, 2)}\n`);
  console.log(`Indexed ${graph.nodes.length} nodes and ${graph.edges.length} edges at ${graph.commit} -> ${out}`);

  if (upload === undefined) return 0;

  const key = process.env.MONDAY_KEY;
  if (!key || key.trim().length === 0) {
    console.error("--upload needs the MONDAY_KEY environment variable.");
    return 3;
  }

  const url = new URL("ingest/graph", upload.endsWith("/") ? upload : `${upload}/`);
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Monday-Key": key },
    body: JSON.stringify(graph),
  });
  console.log(`Upload: ${response.status} ${await response.text()}`);
  return response.ok ? 0 : 4;
}

process.exitCode = await main(process.argv.slice(2));
