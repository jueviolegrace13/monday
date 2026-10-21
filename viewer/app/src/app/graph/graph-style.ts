import { CodeNode, EdgeKind, Severity } from '../api/monday-api';
import { Point, SIZE } from './graph-layout';
import { CONTAINERS, Coverage, GraphModel } from './graph-model';
import { Lens } from './graph-screen';

// How nodes and edges look for a given view: carried over from viewer/artifact/index.html. Colours,
// fonts and sizes come from design/tokens.css (read once per engine). The artifact's canvas literals
// that have no token (edge colours, dimmed node, closed-ring fill, low severity) use the nearest
// existing token instead; see the Task 10 report for the list.

/** Reads a design token from the page (`--accent` → `#3CF0A8`). */
export type TokenReader = (name: string) => string;

export const cssTokens: TokenReader = (name) =>
  getComputedStyle(document.documentElement).getPropertyValue(name).trim();

export interface Palette {
  kind: (kind: string) => string;
  accent: string;
  warn: string;
  ok: string;
  bad: string;
  text: string;
  bg: string;
  dim: string;
  dimLabel: string;
  closed: string;
  lit: string;
  lowSeverity: string;
  coverage: Record<Coverage, string>;
  edge: Record<EdgeKind, string>;
  fontDisplay: string;
  fontMono: string;
  /** Label sizes in px, from the type scale. */
  label: { app: number; module: number; container: number; leaf: number; badge: number };
  /** Camera moves (--dur-slow), in ms. */
  camera: number;
}

const remPx = (value: string) => {
  const n = parseFloat(value);
  return value.endsWith('rem') ? n * 16 : n;
};
const ms = (value: string) => (value.endsWith('ms') ? parseFloat(value) : parseFloat(value) * 1000);

export function readPalette(token: TokenReader): Palette {
  return {
    kind: (k) => token('--kind-' + k.toLowerCase()),
    accent: token('--accent'),
    warn: token('--status-warn'),
    ok: token('--status-ok'),
    bad: token('--status-bad'),
    text: token('--text'),
    bg: token('--bg'),
    dim: token('--border'),
    dimLabel: token('--text-faint'),
    closed: token('--surface-glass'),
    lit: token('--text'),
    lowSeverity: token('--status-warn'),
    coverage: {
      direct: token('--coverage-covered'),
      indirect: token('--kind-app'),
      none: token('--coverage-uncovered'),
    },
    edge: {
      Contains: token('--border'),
      Next: token('--accent'),
      Calls: token('--border-strong'),
      DependsOn: token('--kind-module'),
      Implements: token('--kind-type'),
      Covers: token('--kind-test'),
      HandledBy: token('--status-warn'),
    },
    fontDisplay: token('--font-display'),
    fontMono: token('--font-mono'),
    label: {
      app: remPx(token('--text-lg')),
      module: remPx(token('--text-md')),
      container: remPx(token('--text-sm')),
      leaf: remPx(token('--text-xs')),
      badge: remPx(token('--text-xs')),
    },
    camera: ms(token('--dur-slow')),
  };
}

const SHAPE: Record<string, string> = {
  Endpoint: 'diamond',
  Type: 'square',
  RouteGroup: 'triangle',
  Middleware: 'hexagon',
};
export const EDGE_LINE: Record<EdgeKind, { dashes: false | number[]; width: number }> = {
  Contains: { dashes: false, width: 0.6 },
  Next: { dashes: false, width: 1.4 },
  Calls: { dashes: false, width: 0.8 },
  DependsOn: { dashes: [2, 4], width: 0.7 },
  Implements: { dashes: [6, 3], width: 0.8 },
  Covers: { dashes: [2, 3], width: 0.7 },
  HandledBy: { dashes: false, width: 1.4 },
};
export const EDGE_LABEL: Partial<Record<EdgeKind, string>> = {
  HandledBy: 'Handled by',
  DependsOn: 'Depends on',
  Next: 'Request flow',
};

/** What the styles depend on: the engine's current view. */
export interface ViewState {
  lens: Lens;
  focusId: string | null;
  selected: string | null;
  lit: ReadonlySet<string>;
  expanded: ReadonlySet<string>;
  pulsing: ReadonlySet<string>;
  liveNodes: ReadonlySet<string>;
  pos: ReadonlyMap<string, Point>;
}

export function sevColor(p: Palette, sev: Severity): string {
  return sev === 'Critical' || sev === 'High' ? p.bad : sev === 'Medium' ? p.warn : p.lowSeverity;
}

export function nodeStyle(
  model: GraphModel,
  view: ViewState,
  p: Palette,
  n: CodeNode,
): Record<string, unknown> {
  const collapsed = model.isContainer(n) && !view.expanded.has(n.id);
  const near = view.focusId ? model.neighbours(view.focusId) : null;
  let c = p.kind(n.kind);
  let dim = false;
  let glow = n.kind === 'App' || n.kind === 'Module';
  if (view.lens === 'security') {
    const worst = collapsed ? model.worstUnder(n.id) : model.worstAt(n.id);
    if (worst) {
      c = sevColor(p, worst);
      glow = true;
    } else if (n.kind === 'Endpoint' || n.kind === 'RouteGroup')
      c = model.isGuarded(n) ? p.ok : p.bad;
    else if (!CONTAINERS.has(n.kind) && n.kind !== 'Middleware') dim = true;
  } else if (view.lens === 'coverage') {
    const st = model.cov.get(n.id);
    if (st && !collapsed) {
      c = p.coverage[st];
      glow = st === 'none';
    } else {
      const f = model.covUnder(n.id);
      if (f === null) dim = !CONTAINERS.has(n.kind);
      else c = f >= 0.7 ? p.coverage.direct : f >= 0.3 ? p.warn : p.coverage.none;
    }
  } else if (view.lens === 'live') {
    if (
      !CONTAINERS.has(n.kind) &&
      n.kind !== 'Endpoint' &&
      n.kind !== 'Middleware' &&
      !view.liveNodes.has(n.id)
    )
      dim = true;
  }
  if (near && !near.has(n.id)) dim = true;
  if (view.lit.size && !view.lit.has(n.id) && !(near && near.has(n.id))) dim = true;
  const isLit = view.lit.has(n.id);
  const isSel = view.selected === n.id;
  const isFocus = view.focusId === n.id;
  const isPulse = view.pulsing.has(n.id);
  if (isLit || isSel || isFocus || isPulse) {
    glow = true;
    dim = false;
  }
  const color = dim ? p.dim : c;
  const leaves = model.leafCount.get(n.id) ?? 1;
  const size =
    (SIZE[n.kind] ?? 5) +
    (collapsed ? Math.min(14, Math.sqrt(leaves) * 1.6) : 0) +
    (isFocus ? 4 : isLit || isPulse ? 3 : 0);
  const label = model.displayName(n) + (collapsed ? `  +${model.leafCount.get(n.id) ?? 0}` : '');
  const pos = view.pos.get(n.id)!;
  // A pulsing node changes more than colour: a three-times heavier outline, and the engine rings it.
  const border = isPulse ? p.accent : isLit ? p.warn : color;
  const face = CONTAINERS.has(n.kind) && n.kind !== 'Type' ? p.fontDisplay : p.fontMono;
  return {
    id: n.id,
    label,
    x: pos.x,
    y: pos.y,
    fixed: true,
    title: `${n.kind}: ${n.kind === 'Endpoint' ? n.route : n.name}${collapsed ? ' — click to open' : ''}`,
    shape: SHAPE[n.kind] ?? 'dot',
    size,
    borderWidth: isPulse ? 3 : collapsed ? 2 : 1,
    shapeProperties: { borderDashes: collapsed && !isPulse ? [3, 3] : false },
    color: {
      background: isLit || isFocus ? p.lit : collapsed ? p.closed : color,
      border,
      highlight: { background: p.lit, border: c },
      hover: { background: p.lit, border: c },
    },
    shadow: {
      enabled: glow && !dim,
      color: isPulse ? p.accent : isLit ? p.warn : c,
      size: isLit || isFocus || isPulse ? 22 : 12,
      x: 0,
      y: 0,
    },
    font: {
      size:
        n.kind === 'App'
          ? p.label.app
          : n.kind === 'Module'
            ? p.label.module
            : CONTAINERS.has(n.kind)
              ? p.label.container
              : p.label.leaf,
      color: dim ? p.dimLabel : p.text,
      face,
      strokeWidth: 4,
      strokeColor: p.bg,
    },
  };
}

export interface EdgeItem {
  id: string;
  from: string;
  to: string;
  kind: EdgeKind;
  count: number;
}

/** Edges between hidden things are drawn between the nearest visible boxes, thicker when they bundle several. */
export function edgeList(
  model: GraphModel,
  isVisible: (id: string) => boolean,
  repOf: (id: string) => string | null,
): EdgeItem[] {
  const list: EdgeItem[] = [];
  const agg = new Map<string, EdgeItem>();
  for (const n of model.nodes)
    if (n.parentId && isVisible(n.id) && isVisible(n.parentId))
      list.push({ id: 'p:' + n.id, from: n.parentId, to: n.id, kind: 'Contains', count: 1 });
  for (const e of model.edges) {
    const a = repOf(e.from);
    const b = repOf(e.to);
    if (!a || !b || a === b || model.isAncestor(a, b) || model.isAncestor(b, a)) continue;
    const key = a + '→' + b + '|' + e.kind;
    const x = agg.get(key);
    if (x) x.count++;
    else agg.set(key, { id: 'e:' + key, from: a, to: b, kind: e.kind, count: 1 });
  }
  return list.concat([...agg.values()]);
}

export function edgeStyle(view: ViewState, p: Palette, e: EdgeItem): Record<string, unknown> {
  const s = EDGE_LINE[e.kind] ?? EDGE_LINE.Calls;
  const touchesFocus = !!view.focusId && (e.from === view.focusId || e.to === view.focusId);
  const hot =
    (view.lit.size > 0 && view.lit.has(e.from) && view.lit.has(e.to) && e.kind !== 'Contains') ||
    touchesFocus;
  const faded =
    (view.lit.size > 0 && !hot) ||
    (!!view.focusId && !touchesFocus) ||
    (view.lens === 'security' && !['HandledBy', 'Calls', 'Next'].includes(e.kind));
  const width = (hot ? 2.2 : s.width) * (e.count > 1 ? Math.min(3, 1 + Math.log2(e.count) / 2) : 1);
  return {
    id: e.id,
    from: e.from,
    to: e.to,
    dashes: s.dashes,
    width,
    title: e.count > 1 ? `${e.count} ${e.kind} links` : e.kind,
    color: {
      color: hot ? p.warn : p.edge[e.kind],
      opacity: faded ? 0.08 : e.kind === 'Contains' ? 0.45 : 0.6,
    },
    arrows: e.kind === 'Contains' ? undefined : { to: { enabled: true, scaleFactor: 0.35 } },
  };
}
