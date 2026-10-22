import { createHash } from "node:crypto";

// The same shapes as Monday.Domain.CodeGraph (CodeNode, CodeEdge, SecurityFinding), as Monday's
// ingest endpoint takes them: camelCase, enums as strings, nulls left out.

export const NODE_KINDS = ["App", "Module", "Feature", "Type", "Method", "Endpoint", "Test", "Middleware", "RouteGroup"] as const;
export type NodeKind = (typeof NODE_KINDS)[number];

export type EdgeKind = "Contains" | "Calls" | "DependsOn" | "Implements" | "Covers" | "HandledBy" | "Next";

export type Severity = "Low" | "Medium" | "High";

export interface CodeNode {
  id: string;
  kind: NodeKind;
  name: string;
  parentId?: string;
  file?: string;
  startLine?: number;
  endLine?: number;
  code?: string;
  codeHash?: string;
  route?: string;
  guards: string[];
}

export interface CodeEdge {
  from: string;
  to: string;
  kind: EdgeKind;
}

export interface SecurityFinding {
  rule: string;
  severity: Severity;
  nodeId: string;
  title: string;
  why: string;
  fix: string;
  file?: string;
  line?: number;
}

/** What the indexer writes and uploads: Monday's GraphUpload. */
export interface GraphFile {
  commit: string;
  nodes: CodeNode[];
  edges: CodeEdge[];
  findings: SecurityFinding[];
}

const SEVERITY_ORDER: Record<Severity, number> = { Low: 0, Medium: 1, High: 2 };

/** Same as CodeNode.HashCode in the domain: SHA-256 of the code with "\n" line endings, first 16 hex digits, upper case. */
export function hashCode(code: string): string {
  return createHash("sha256").update(code.replace(/\r\n|\r|\u2028|\u2029|\u0085/g, "\n"), "utf8").digest("hex").slice(0, 16).toUpperCase();
}

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/** Nodes by kind then id, edges by from then to, findings worst first: the same file for the same code. */
export function sortGraph(graph: GraphFile): GraphFile {
  const seen = new Set<string>();
  const edges = graph.edges
    .filter((e) => e.from !== e.to)
    .filter((e) => {
      const key = `${e.from}\u0000${e.to}\u0000${e.kind}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    })
    .sort((a, b) => ordinal(a.from, b.from) || ordinal(a.to, b.to) || ordinal(a.kind, b.kind));
  return {
    commit: graph.commit,
    nodes: [...graph.nodes].sort((a, b) => NODE_KINDS.indexOf(a.kind) - NODE_KINDS.indexOf(b.kind) || ordinal(a.id, b.id)),
    edges,
    findings: [...graph.findings].sort(
      (a, b) => SEVERITY_ORDER[b.severity] - SEVERITY_ORDER[a.severity] || ordinal(a.nodeId, b.nodeId) || ordinal(a.rule, b.rule),
    ),
  };
}
