import { GraphModel } from './graph-model';

// Tidy neighbourhoods: nested rings that make room for what's open. Every open thing arranges its
// children in a ring around itself, and each child takes as much of the ring as its own label or
// open contents need. Rings grow when something inside opens, so nodes and labels never pile up.
// The order is fixed, so the same map always comes out the same way.
// Carried over unchanged from viewer/artifact/index.html.

export interface Point {
  x: number;
  y: number;
}

const LABEL_PX = 6.8;
const GAP = 16;

/** Node radius on the map, by kind: graph geometry, not chrome. */
export const SIZE: Record<string, number> = {
  App: 30,
  Module: 20,
  Feature: 14,
  Type: 9,
  RouteGroup: 11,
  Middleware: 8,
  Method: 5,
  Endpoint: 9,
  Test: 5,
};

export function footprint(model: GraphModel, expanded: ReadonlySet<string>, id: string): number {
  const n = model.byId.get(id)!;
  const label = model.displayName(n) + (model.isContainer(n) && !expanded.has(id) ? '  +000' : '');
  return Math.max((SIZE[n.kind] ?? 5) + 14, Math.min(120, (label.length * LABEL_PX) / 2) + 6);
}

/**
 * Positions for everything on screen. `forced` holds things shown only because of a focus, a
 * highlight or a pulse while their ring is closed: they sit in a small ring of their own beside the
 * closed ring that holds them, instead of on top of it.
 */
export function layout(
  model: GraphModel,
  expanded: ReadonlySet<string>,
  forced: ReadonlySet<string>,
): Map<string, Point> {
  const pos = new Map<string, Point>();
  const R = new Map<string, number>();
  const ring = new Map<string, number>();
  const openKids = (id: string) => (expanded.has(id) ? (model.kids.get(id) ?? []) : []);
  const size = (id: string): number => {
    const c = openKids(id);
    if (!c.length) {
      R.set(id, footprint(model, expanded, id));
      return R.get(id)!;
    }
    const rs = c.map(size);
    const own = footprint(model, expanded, id) * 0.6;
    const need = rs.reduce((t, r) => t + 2 * (r + GAP), 0) / (2 * Math.PI);
    const rho = Math.max(need, own + Math.max(...rs) + GAP, 70);
    ring.set(id, rho);
    R.set(id, rho + Math.max(...rs) + GAP);
    return R.get(id)!;
  };
  const place = (id: string, x: number, y: number, start: number) => {
    pos.set(id, { x, y });
    const c = openKids(id);
    if (!c.length) return;
    const total = c.reduce((t, k) => t + R.get(k)! + GAP, 0);
    let a = start;
    c.forEach((k) => {
      const span = ((R.get(k)! + GAP) / total) * Math.PI * 2;
      const mid = a + span / 2;
      // Grandchildren open away from the parent.
      place(k, x + Math.cos(mid) * ring.get(id)!, y + Math.sin(mid) * ring.get(id)!, mid + Math.PI);
      a += span;
    });
  };
  model.roots.forEach((id, i) => {
    size(id);
    place(id, i * 4000, 0, -Math.PI / 2);
  });

  const extras = new Map<string, string[]>();
  forced.forEach((id) => {
    if (pos.has(id) || !model.byId.has(id)) return;
    let rep = model.byId.get(id)!.parentId;
    while (rep && !pos.has(rep)) rep = model.byId.get(rep)?.parentId;
    if (!rep) return;
    if (!extras.has(rep)) extras.set(rep, []);
    extras.get(rep)!.push(id);
  });
  extras.forEach((list, rep) => {
    const base = pos.get(rep)!;
    const r0 = (R.get(rep) ?? footprint(model, expanded, rep)) + 50 + list.length * 6;
    list.forEach((id, i) => {
      const a = -Math.PI / 2 + (i / list.length) * Math.PI * 2;
      pos.set(id, { x: base.x + Math.cos(a) * r0, y: base.y + Math.sin(a) * r0 });
    });
  });
  return pos;
}
