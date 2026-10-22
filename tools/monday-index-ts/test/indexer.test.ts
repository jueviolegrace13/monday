import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { type GraphFile, hashCode } from "../src/graph.ts";
import { RepoIndexer } from "../src/indexer.ts";
import { nextFile } from "../src/next-routes.ts";

const fixture = path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "next-app");
const graph: GraphFile = new RepoIndexer(fixture, "Fixture").index("abc123");

const node = (id: string) => graph.nodes.find((n) => n.id === id);
const has = (from: string, to: string, kind: string) => graph.edges.some((e) => e.from === from && e.to === to && e.kind === kind);

test("routes come from file paths, like Monday's C# routes", () => {
  assert.deepEqual(nextFile("src/app/api/trades/[id]/route.ts"), { kind: "route", path: "/api/trades/{id}" });
  assert.deepEqual(nextFile("app/(app)/journal/page.tsx"), { kind: "page", path: "/journal" });
  assert.deepEqual(nextFile("src/app/page.tsx"), { kind: "page", path: "/" });
  assert.deepEqual(nextFile("src/app/api/auth/[...nextauth]/route.ts"), { kind: "route", path: "/api/auth/{*nextauth}" });
  assert.deepEqual(nextFile("pages/api/users/index.ts"), { kind: "pages-api", path: "/api/users" });
  assert.equal(nextFile("src/app/_lib/page.tsx"), null);
  assert.equal(nextFile("src/lib/money.ts"), null);
});

test("the hash matches the domain's CodeNode.HashCode", () => {
  // SHA-256("a\nb") upper case, first 16 hex digits; \r\n is read as \n.
  assert.equal(hashCode("a\r\nb"), hashCode("a\nb"));
  assert.equal(hashCode("a\nb"), "7E18F737311B2DC3");
});

test("folders become modules and features, files hold their functions", () => {
  assert.equal(node("app")?.kind, "App");
  assert.equal(node("module:server")?.parentId, "app");
  assert.equal(node("feature:server/services")?.parentId, "module:server");
  assert.equal(node("file:src/server/services/trade-service.ts")?.parentId, "feature:server/services");
  assert.equal(node("file:src/server/auth.ts")?.parentId, "module:server");
  assert.equal(node("file:src/proxy.ts")?.parentId, "module:src");

  const requireUserId = node("src/server/auth.ts#requireUserId");
  assert.equal(requireUserId?.kind, "Method");
  assert.equal(requireUserId?.startLine, 5);
  assert.equal(requireUserId?.codeHash, hashCode(requireUserId!.code!));
});

test("object services and classes are types with methods", () => {
  assert.equal(node("src/server/services/trade-service.ts#tradeService")?.kind, "Type");
  assert.equal(node("src/server/services/trade-service.ts#tradeService.get")?.parentId, "src/server/services/trade-service.ts#tradeService");
  assert.equal(node("src/server/services/trade-service.ts#tradeService.remove")?.kind, "Method");
  assert.equal(node("src/server/services/trade-service.ts#TradeStore.key")?.kind, "Method");
});

test("calls resolve through imports and path aliases", () => {
  assert.ok(has("src/server/auth.ts#requireUserId", "src/server/auth.ts#auth", "Calls"));
  assert.ok(has("src/server/services/trade-service.ts#tradeService.get", "src/lib/money.ts#round", "Calls"));
  assert.ok(has("src/server/services/trade-service.ts#tradeService.remove", "src/server/services/trade-service.ts#TradeStore.constructor", "Calls"));
  assert.ok(has("src/server/services/trade-service.ts#tradeService.remove", "src/server/services/trade-service.ts#TradeStore.key", "Calls"));
  assert.ok(has("src/components/money-label.tsx#MoneyLabel", "src/lib/money.ts#formatMoney", "Calls"));
  assert.ok(has("src/app/(app)/journal/page.tsx#JournalPage", "src/components/money-label.tsx#MoneyLabel", "Calls"), "JSX use is a call");
  assert.ok(has("file:src/server/services/trade-service.ts", "file:src/lib/money.ts", "DependsOn"));
});

test("tests cover the code they call", () => {
  const id = "src/lib/money.test.ts#test:formatMoney › rounds to cents";
  assert.equal(node(id)?.kind, "Test");
  assert.ok(has(id, "src/lib/money.ts#formatMoney", "Covers"));
});

test("route handlers, pages and server actions are endpoints with their guards", () => {
  const get = node("endpoint:GET /api/trades/{id}");
  assert.equal(get?.kind, "Endpoint");
  assert.deepEqual(get?.guards, ["Auth:auth"]);
  assert.ok(has("endpoint:GET /api/trades/{id}", "src/app/api/trades/[id]/route.ts#GET", "HandledBy"));

  assert.deepEqual(node("endpoint:DELETE /api/trades/{id}")?.guards, []);
  assert.deepEqual(node("endpoint:GET /journal")?.guards, ["Auth:requireUserId"]);
  assert.ok(has("endpoint:GET /journal", "src/app/(app)/journal/page.tsx#JournalPage", "HandledBy"));

  assert.deepEqual(node("endpoint:ACTION trade-actions/deleteTradeAction")?.guards, ["Auth:requireUserId"]);
  assert.ok(has("endpoint:ACTION trade-actions/publicAction", "src/server/actions/trade-actions.ts#publicAction", "HandledBy"));
});

test("middleware is the request pipeline in front of every endpoint", () => {
  const middleware = node("middleware:fixture-app:proxy");
  assert.equal(middleware?.kind, "Middleware");
  assert.equal(middleware?.parentId, "pipeline:fixture-app");
  assert.deepEqual(middleware?.guards, ["matcher:/((?!_next/static).*)"]);
  assert.ok(has("middleware:fixture-app:proxy", "endpoint:GET /journal", "Next"));
  assert.ok(has("middleware:fixture-app:proxy", "src/proxy.ts#proxy", "HandledBy"));
});

test("threats are found on the node they live on", () => {
  const rules = graph.findings.map((f) => `${f.rule} ${f.nodeId}`);
  assert.ok(rules.includes("write-without-auth endpoint:DELETE /api/trades/{id}"));
  assert.ok(rules.includes("write-without-auth endpoint:ACTION trade-actions/publicAction"));
  assert.ok(!rules.includes("write-without-auth endpoint:ACTION trade-actions/deleteTradeAction"));
  assert.ok(rules.includes("html-from-data src/components/money-label.tsx#NoteHtml"));
  assert.ok(rules.includes("hardcoded-secret file:src/server/config.ts"));
  assert.ok(!rules.some((r) => r.includes("endpoint:GET /journal")), "pages are left to the middleware");
  assert.equal(graph.findings[0].severity, "High");
});

test("every edge and parent points at a node, as the domain requires", () => {
  const ids = new Set(graph.nodes.map((n) => n.id));
  assert.equal(ids.size, graph.nodes.length);
  for (const n of graph.nodes) if (n.parentId) assert.ok(ids.has(n.parentId), n.id);
  for (const e of graph.edges) assert.ok(ids.has(e.from) && ids.has(e.to), `${e.from} -> ${e.to}`);
  for (const f of graph.findings) assert.ok(ids.has(f.nodeId), f.nodeId);
});

test("the same code gives the same graph", () => {
  assert.deepEqual(new RepoIndexer(fixture, "Fixture").index("abc123"), graph);
});
