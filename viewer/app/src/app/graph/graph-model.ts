import {
  CodeEdge,
  CodeNode,
  GraphView,
  NodeKind,
  SecurityFinding,
  Severity,
} from '../api/monday-api';

// The code graph as the viewer reads it: indexes, names, coverage and the request chain of each
// endpoint. Carried over from viewer/artifact/index.html with the same rules; only the shape changed
// (a class around the data instead of globals).

export const KINDS: readonly NodeKind[] = [
  'App',
  'Module',
  'Feature',
  'Type',
  'RouteGroup',
  'Middleware',
  'Method',
  'Endpoint',
  'Test',
];
export const CONTAINERS: ReadonlySet<NodeKind> = new Set<NodeKind>([
  'App',
  'Module',
  'Feature',
  'Type',
  'RouteGroup',
]);
const AUTH_GUARDS = /^(RequireAuthorization|RequireReporterKey)/;
export const SEV_RANK: Record<Severity, number> = { Critical: 4, High: 3, Medium: 2, Low: 1 };

export type Coverage = 'direct' | 'indirect' | 'none';
export const COV_LABEL: Record<Coverage, string> = {
  direct: 'Tested directly',
  indirect: 'Reached by a test',
  none: 'No test reaches it',
};

/** The request chain one endpoint walks; `gate` is where an unauthenticated call is stopped. */
export interface Chain {
  ids: string[];
  gate: number;
}

export class GraphModel {
  readonly nodes: CodeNode[];
  readonly edges: CodeEdge[];
  readonly commit: string;
  readonly byId = new Map<string, CodeNode>();
  readonly out = new Map<string, CodeEdge[]>();
  readonly inn = new Map<string, CodeEdge[]>();
  readonly kids = new Map<string, string[]>();
  readonly leafCount = new Map<string, number>();
  readonly findings: SecurityFinding[];
  readonly findingsByNode = new Map<string, SecurityFinding[]>();
  readonly cov = new Map<string, Coverage>();
  readonly roots: string[];

  constructor(graph: GraphView) {
    this.nodes = graph.nodes;
    this.edges = graph.edges;
    this.commit = graph.commit;
    for (const n of this.nodes) {
      this.byId.set(n.id, n);
      this.out.set(n.id, []);
      this.inn.set(n.id, []);
      this.kids.set(n.id, []);
    }
    for (const e of this.edges) {
      if (this.out.has(e.from) && this.inn.has(e.to)) {
        this.out.get(e.from)!.push(e);
        this.inn.get(e.to)!.push(e);
      }
    }
    for (const n of this.nodes)
      if (n.parentId && this.kids.has(n.parentId)) this.kids.get(n.parentId)!.push(n.id);
    this.findings = graph.findings ?? [];
    for (const f of this.findings) {
      if (!this.findingsByNode.has(f.nodeId)) this.findingsByNode.set(f.nodeId, []);
      this.findingsByNode.get(f.nodeId)!.push(f);
    }
    this.roots = this.nodes
      .filter((n) => !n.parentId || !this.byId.has(n.parentId))
      .map((n) => n.id);
    this.computeCoverage();
    this.layoutTree();
  }

  count(kind: NodeKind): number {
    return this.nodes.filter((n) => n.kind === kind).length;
  }

  /** Children in a fixed order (kind, then name), and how many leaves each subtree holds. */
  private layoutTree(): void {
    const rank = (id: string) => KINDS.indexOf(this.byId.get(id)!.kind);
    this.kids.forEach((list) =>
      list.sort(
        (a, b) => rank(a) - rank(b) || this.byId.get(a)!.name.localeCompare(this.byId.get(b)!.name),
      ),
    );
    const weigh = (id: string): number => {
      const c = this.kids.get(id)!;
      const w = c.length ? c.reduce((s, k) => s + weigh(k), 0) : 1;
      this.leafCount.set(id, w);
      return w;
    };
    this.roots.forEach(weigh);
  }

  // ---------- structure ----------

  isContainer(n: CodeNode): boolean {
    return CONTAINERS.has(n.kind) && (this.kids.get(n.id) ?? []).length > 0;
  }

  ancestors(id: string): string[] {
    const list: string[] = [];
    let p = this.byId.get(id)?.parentId;
    while (p && this.byId.has(p)) {
      list.push(p);
      p = this.byId.get(p)!.parentId;
    }
    return list;
  }

  isAncestor(a: string, b: string): boolean {
    return this.ancestors(b).includes(a);
  }

  neighbours(id: string): Set<string> {
    const set = new Set([id]);
    (this.out.get(id) ?? []).forEach((e) => set.add(e.to));
    (this.inn.get(id) ?? []).forEach((e) => set.add(e.from));
    const n = this.byId.get(id);
    if (n?.parentId) set.add(n.parentId);
    (this.kids.get(id) ?? []).forEach((k) => set.add(k));
    return set;
  }

  under(id: string): string[] {
    const list: string[] = [];
    const walk = (i: string) =>
      (this.kids.get(i) ?? []).forEach((k) => {
        list.push(k);
        walk(k);
      });
    walk(id);
    return list;
  }

  related(id: string, kind: CodeEdge['kind'], dir: 'in' | 'out'): CodeNode[] {
    const list = (dir === 'out' ? this.out.get(id) : this.inn.get(id)) ?? [];
    return list
      .filter((e) => e.kind === kind)
      .map((e) => this.byId.get(dir === 'out' ? e.to : e.from))
      .filter((n): n is CodeNode => !!n);
  }

  moduleOf(n: CodeNode | undefined): CodeNode | undefined {
    let cur = n;
    while (cur && cur.kind !== 'Module')
      cur = cur.parentId ? this.byId.get(cur.parentId) : undefined;
    return cur;
  }

  // ---------- names ----------

  shortName(n: CodeNode): string {
    if (n.kind === 'Module') return n.name.replace(/^Monday\./, '');
    if (n.kind === 'Endpoint' || n.kind === 'RouteGroup') return n.route || n.name;
    if (n.kind === 'Middleware') return n.name.replace(/^Use/, '');
    return n.name;
  }

  /**
   * What a node is called on the map: short enough to read. An endpoint inside a route group drops
   * the group's prefix ("PUT /{id}/members/{member}" under /projects) and route constraints like ":guid".
   */
  displayName(n: CodeNode): string {
    if (n.kind === 'Endpoint') {
      const p = n.parentId ? this.byId.get(n.parentId) : undefined;
      const [verb, ...rest] = (n.route || n.name).split(' ');
      let path = rest.join(' ').replace(/:[a-z]+(\([^)]*\))?}/gi, '}');
      if (p && p.kind === 'RouteGroup' && p.route && path.startsWith(p.route))
        path = path.slice(p.route.length) || '/';
      return `${verb} ${path}`;
    }
    return this.shortName(n);
  }

  // ---------- security ----------

  isGuarded(n: CodeNode): boolean {
    return (
      (n.guards ?? []).some((g) => AUTH_GUARDS.test(g)) &&
      !(n.guards ?? []).includes('AllowAnonymous')
    );
  }

  worstAt(id: string): Severity | null {
    return (this.findingsByNode.get(id) ?? []).reduce<Severity | null>(
      (w, f) => (!w || SEV_RANK[f.severity] > SEV_RANK[w] ? f.severity : w),
      null,
    );
  }

  worstUnder(id: string): Severity | null {
    return [id, ...this.under(id)]
      .map((i) => this.worstAt(i))
      .reduce<Severity | null>((w, s) => (s && (!w || SEV_RANK[s] > SEV_RANK[w]) ? s : w), null);
  }

  sevCounts(): Record<Severity, number> {
    const c: Record<Severity, number> = { Critical: 0, High: 0, Medium: 0, Low: 0 };
    this.findings.forEach((f) => c[f.severity]++);
    return c;
  }

  // ---------- coverage (static: which product methods a test reaches through calls) ----------

  private computeCoverage(): void {
    const direct = new Set<string>();
    const reach = new Set<string>();
    for (const t of this.nodes.filter((n) => n.kind === 'Test'))
      for (const e of this.out.get(t.id) ?? []) if (e.kind === 'Covers') direct.add(e.to);
    const queue = [...direct];
    queue.forEach((id) => reach.add(id));
    while (queue.length) {
      const id = queue.shift()!;
      for (const e of this.out.get(id) ?? [])
        if (e.kind === 'Calls' && !reach.has(e.to)) {
          reach.add(e.to);
          queue.push(e.to);
        }
    }
    for (const n of this.nodes) {
      if (n.kind === 'Method' && !n.id.startsWith('startup:') && !this.isTestModule(n))
        this.cov.set(n.id, direct.has(n.id) ? 'direct' : reach.has(n.id) ? 'indirect' : 'none');
      if (n.kind === 'Endpoint') {
        const handled = (this.out.get(n.id) ?? [])
          .filter((e) => e.kind === 'HandledBy' || e.kind === 'Calls')
          .map((e) => e.to);
        this.cov.set(
          n.id,
          handled.some((h) => direct.has(h))
            ? 'direct'
            : handled.some((h) => reach.has(h))
              ? 'indirect'
              : 'none',
        );
      }
    }
  }

  private isTestModule(n: CodeNode): boolean {
    const m = this.moduleOf(n);
    return !!m && /\.Tests$/.test(m.name);
  }

  covUnder(id: string): number | null {
    const ms = [id, ...this.under(id)].filter(
      (i) => this.cov.has(i) && this.byId.get(i)!.kind === 'Method',
    );
    return ms.length ? ms.filter((i) => this.cov.get(i) !== 'none').length / ms.length : null;
  }

  covSummary(): { total: number; reached: number; none: number; pct: number } {
    const items = [...this.cov.entries()].filter(([id]) => this.byId.get(id)!.kind === 'Method');
    const reached = items.filter(([, s]) => s !== 'none').length;
    return {
      total: items.length,
      reached,
      none: items.length - reached,
      pct: items.length ? Math.round((reached / items.length) * 100) : 0,
    };
  }

  // ---------- request path ----------

  private pipelineSteps(mod: CodeNode | undefined): CodeNode[] {
    if (!mod) return [];
    const steps = this.nodes.filter(
      (n) => n.kind === 'Middleware' && n.parentId === 'pipeline:' + mod.name,
    );
    const order: string[] = [];
    let cur = steps.find(
      (m) =>
        !(this.inn.get(m.id) ?? []).some(
          (e) => e.kind === 'Next' && this.byId.get(e.from)?.kind === 'Middleware',
        ),
    );
    while (cur && !order.includes(cur.id)) {
      order.push(cur.id);
      const nx = (this.out.get(cur.id) ?? []).find(
        (e) => e.kind === 'Next' && this.byId.get(e.to)?.kind === 'Middleware',
      );
      cur = nx && this.byId.get(nx.to);
    }
    return order.map((i) => this.byId.get(i)!);
  }

  private groupOf(ep: CodeNode): CodeNode | null {
    const p = ep.parentId ? this.byId.get(ep.parentId) : undefined;
    return p && p.kind === 'RouteGroup' ? p : null;
  }

  private guardMethodsOf(node: CodeNode): CodeNode[] {
    return this.related(node.id, 'Calls', 'out').filter((m) =>
      (node.guards ?? []).some((g) => g.startsWith(m.name)),
    );
  }

  /** Each request walks the chain the Request path shows: middleware → group guards → endpoint → handler → calls. */
  chainFor(ep: CodeNode): Chain {
    const group = this.groupOf(ep);
    const ids = this.pipelineSteps(this.moduleOf(ep)).map((m) => m.id);
    if (group) {
      ids.push(group.id);
      this.guardMethodsOf(group).forEach((m) => ids.push(m.id));
    }
    const gate = ids.length;
    ids.push(ep.id);
    this.related(ep.id, 'Calls', 'out')
      .filter((m) => (ep.guards ?? []).some((g) => g.startsWith(m.name)))
      .forEach((m) => ids.push(m.id));
    let cur: CodeNode | undefined =
      this.related(ep.id, 'HandledBy', 'out')[0] ??
      this.related(ep.id, 'Calls', 'out').find((m) => !ids.includes(m.id));
    for (let d = 0; cur && d < 3; d++) {
      ids.push(cur.id);
      const from: CodeNode = cur;
      cur = this.related(from.id, 'Calls', 'out').find((m) => !ids.includes(m.id));
    }
    return { ids, gate: Math.min(gate, ids.length - 1) };
  }
}

/** Guards the indexer emits that take a protection away (src/Monday.Indexer/RepoIndexer.cs). */
const OPEN_GUARDS: ReadonlySet<string> = new Set([
  'AllowAnonymous',
  'DisableAntiforgery',
  'DisableRateLimiting',
]);

/** True for a guard that opens an endpoint up; the name may carry arguments: `AllowAnonymous()`. */
export const isOpenGuard = (guard: string): boolean => OPEN_GUARDS.has(guard.split('(')[0].trim());
