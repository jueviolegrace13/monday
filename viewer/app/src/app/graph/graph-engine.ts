import { GraphView, TrafficView } from '../api/monday-api';
import { Point } from './graph-layout';
import { layout } from './graph-layout';
import { LiveRequests } from './graph-live';
import { GraphModel } from './graph-model';
import { GraphScreen, Lens } from './graph-screen';
import { EdgeItem, Palette, ViewState, edgeList, edgeStyle, nodeStyle } from './graph-style';
import { VisDataSet, VisLib, VisNetwork } from './vis';

// The graph on screen: what is open, focused, lit and selected, and a history so Back (or Esc)
// always returns. Extracted from viewer/artifact/index.html with the same behaviour; what is new is
// the wiring (data comes in through load, changes go out through onSelect and onChange) and pulse.
// Layout lives in graph-layout.ts, looks in graph-style.ts, the Live lens in graph-live.ts.

/** What the chrome around the canvas shows: the lens, the breadcrumb and whether Back can go back. */
export interface EngineState {
  lens: Lens;
  crumb: string;
  canBack: boolean;
  pulsing: string[];
}

interface Snapshot {
  expanded: string[];
  focusId: string | null;
  lit: string[];
  selected: string | null;
  /** Set only when the step changed the lens (an agent's set_lens or replay), so Back restores it. */
  lens?: Lens;
}

const HISTORY = 60;

export class GraphEngine implements GraphScreen {
  private model: GraphModel | null = null;
  private network: VisNetwork | null = null;
  private nodesDS: VisDataSet<Record<string, unknown>> | null = null;
  private edgesDS: VisDataSet<Record<string, unknown>> | null = null;
  private shownNodes = new Set<string>();
  private shownEdges = new Set<string>();
  private sizes = new Map<string, number>();

  private lens: Lens = 'structure';
  private selected: string | null = null;
  private lit = new Set<string>();
  private focusId: string | null = null;
  private expanded = new Set<string>(['app']);
  private forced = new Set<string>();
  private pos = new Map<string, Point>();
  private history: Snapshot[] = [];
  private pulsing = new Set<string>();
  private live: LiveRequests | null = null;

  private readonly selectListeners: ((id: string | null) => void)[] = [];
  private readonly changeListeners: ((state: EngineState) => void)[] = [];
  private raf = 0;
  /** Bumped by every navigation, so a path still lighting up stops when something else happens. */
  private run = 0;

  constructor(
    private readonly vis: VisLib,
    private readonly container: HTMLElement,
    private readonly palette: Palette,
    private readonly reduceMotion: boolean,
  ) {}

  // ---------- wiring ----------

  load(graph: GraphView): void {
    this.model = new GraphModel(graph);
    this.live = new LiveRequests(this.model);
    this.history = [];
    this.expanded = new Set(['app']);
    this.focusId = null;
    this.selected = null;
    this.lit = new Set();
    this.pulsing = new Set();
    this.network?.destroy();
    this.nodesDS = new this.vis.DataSet<Record<string, unknown>>([]);
    this.edgesDS = new this.vis.DataSet<Record<string, unknown>>([]);
    this.shownNodes = new Set();
    this.shownEdges = new Set();
    this.network = new this.vis.Network(
      this.container,
      { nodes: this.nodesDS, edges: this.edgesDS },
      {
        physics: { enabled: false },
        interaction: {
          hover: true,
          tooltipDelay: 150,
          hideEdgesOnDrag: true,
          multiselect: false,
          dragNodes: false,
          keyboard: false,
        },
        edges: { smooth: { type: 'curvedCW', roundness: 0.12 }, selectionWidth: 1.2 },
      },
    );
    this.network.on('click', (p) => {
      const id = p.nodes[0];
      if (!id) {
        if (!p.edges.length) this.clearView();
        return;
      }
      const n = this.model!.byId.get(id)!;
      if (this.model!.isContainer(n) && n.kind !== 'App') this.toggleExpand(id);
      else this.select(id, { camera: false });
    });
    this.network.on('doubleClick', (p) => {
      if (p.nodes[0]) this.focus(p.nodes[0]);
    });
    this.network.on('afterDrawing', (ctx) => this.drawOverlay(ctx));
    this.refresh();
    this.network.fit({ animation: false });
  }

  get graph(): GraphModel | null {
    return this.model;
  }

  get selectedId(): string | null {
    return this.selected;
  }

  onSelect(cb: (id: string | null) => void): void {
    this.selectListeners.push(cb);
  }

  onChange(cb: (state: EngineState) => void): void {
    this.changeListeners.push(cb);
  }

  destroy(): void {
    cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.network?.destroy();
    this.network = null;
  }

  // ---------- navigation: expand, focus, back ----------

  toggleExpand(id: string): void {
    if (!this.model) return;
    this.remember();
    if (this.expanded.has(id)) {
      this.expanded.delete(id);
      this.model.under(id).forEach((i) => this.expanded.delete(i));
    } else this.expanded.add(id);
    this.setSelected(id);
    this.refresh();
    if (this.expanded.has(id)) this.fit([id, ...(this.model.kids.get(id) ?? [])]);
  }

  focus(id: string): void {
    if (!this.model?.byId.has(id)) return;
    this.remember();
    this.focusId = id;
    this.lit = new Set();
    this.setSelected(id);
    this.refresh();
    this.fit([...this.model.neighbours(id)].filter((i) => this.shownNodes.has(i)));
  }

  /** Lights a path one node at a time, like the viewer's "Trace this path". */
  highlightPath(nodeIds: string[]): void {
    if (!this.model) return;
    const ids = nodeIds.filter((id) => this.model!.byId.has(id));
    if (!ids.length) return;
    this.remember();
    this.focusId = null;
    const run = this.run;
    const step = (i: number) => {
      if (run !== this.run) return;
      this.lit = new Set(ids.slice(0, i + 1));
      this.refresh();
      if (i + 1 < ids.length) setTimeout(() => step(i + 1), this.palette.camera);
      else this.fit(ids.filter((x) => this.shownNodes.has(x)));
    };
    if (this.reduceMotion) step(ids.length - 1);
    else step(0);
  }

  setLens(lens: Lens): void {
    if (lens === this.lens) return;
    this.remember({ withLens: true });
    this.applyLens(lens);
    this.refresh();
  }

  /** The lens buttons: a person switching lenses is not an undoable step, as in the viewer. */
  chooseLens(lens: Lens): void {
    this.applyLens(lens);
    this.refresh();
  }

  replay(nodeIds: string[]): void {
    if (!this.model) return;
    this.remember({ withLens: true });
    this.applyLens('live');
    this.focusId = null;
    this.lit = new Set(nodeIds.filter((id) => this.model!.byId.has(id)));
    this.refresh();
    if (this.lit.size) this.fit([...this.lit].filter((x) => this.shownNodes.has(x)));
  }

  back(): void {
    if (!this.model) return;
    const last = this.history.pop();
    if (!last) {
      this.clearView();
      return;
    }
    this.restore(last);
    this.refresh();
    this.fit(
      this.focusId
        ? [...this.model.neighbours(this.focusId)].filter((i) => this.shownNodes.has(i))
        : undefined,
    );
  }

  clearView(): void {
    if (!this.focusId && !this.lit.size && !this.selected) return;
    this.remember();
    this.focusId = null;
    this.lit = new Set();
    this.setSelected(null);
    this.network?.unselectAll();
    this.refresh();
  }

  home(): void {
    this.remember();
    this.expanded = new Set(['app']);
    this.focusId = null;
    this.lit = new Set();
    this.setSelected(null);
    this.refresh();
    this.fit();
  }

  expandAll(): void {
    if (!this.model) return;
    this.remember();
    this.model.nodes
      .filter((n) => this.model!.isContainer(n))
      .forEach((n) => this.expanded.add(n.id));
    this.refresh();
    this.network?.fit({ animation: false });
  }

  /** Selects a node, opening the rings that hold it; with camera, the view moves to it. */
  select(id: string, { camera = true } = {}): void {
    if (!this.model?.byId.has(id)) return;
    if (!this.isOpenPath(id)) {
      this.remember();
      this.model.ancestors(id).forEach((a) => this.expanded.add(a));
    }
    this.setSelected(id);
    this.refresh();
    this.network?.selectNodes([id]);
    if (camera) this.network?.focus(id, { scale: 1.1, animation: this.animation() });
  }

  // ---------- pulse ----------

  pulse(nodeIds: string[]): void {
    if (!this.model) return;
    let added = false;
    for (const id of nodeIds)
      if (this.model.byId.has(id) && !this.pulsing.has(id)) {
        this.pulsing.add(id);
        added = true;
      }
    if (!added) return;
    this.refresh();
    this.animate();
  }

  settle(): void {
    if (!this.pulsing.size) return;
    this.pulsing = new Set();
    this.refresh();
    this.network?.redraw();
  }

  // ---------- live ----------

  /** A real request from the live stream; drawn while the Live lens is on. */
  request(view: TrafficView): void {
    if (this.lens !== 'live' || !this.live) return;
    this.live.add(view, performance.now());
    this.animate();
  }

  // ---------- internals ----------

  private applyLens(lens: Lens): void {
    this.lens = lens;
    if (lens !== 'live') this.live?.clear();
    else this.animate();
  }

  private setSelected(id: string | null): void {
    if (this.selected === id) return;
    this.selected = id;
    this.selectListeners.forEach((cb) => cb(id));
  }

  private remember({ withLens = false } = {}): void {
    this.run++;
    this.history.push({
      expanded: [...this.expanded],
      focusId: this.focusId,
      lit: [...this.lit],
      selected: this.selected,
      ...(withLens ? { lens: this.lens } : {}),
    });
    if (this.history.length > HISTORY) this.history.shift();
  }

  private restore(s: Snapshot): void {
    this.run++;
    this.expanded = new Set(s.expanded);
    this.focusId = s.focusId;
    this.lit = new Set(s.lit);
    if (s.lens && s.lens !== this.lens) this.applyLens(s.lens);
    this.setSelected(s.selected);
  }

  private isOpenPath(id: string): boolean {
    return this.model!.ancestors(id).every((a) => this.expanded.has(a));
  }

  private isVisible(id: string): boolean {
    return this.model!.byId.has(id) && (this.isOpenPath(id) || this.forced.has(id));
  }

  private repOf(id: string): string | null {
    let cur: string | undefined = id;
    while (cur && !this.isVisible(cur)) cur = this.model!.byId.get(cur)?.parentId;
    return cur ?? null;
  }

  private computeForced(): void {
    const m = this.model!;
    this.forced = new Set([...this.lit, ...this.pulsing]);
    if (this.selected) this.forced.add(this.selected);
    if (this.focusId) m.neighbours(this.focusId).forEach((i) => this.forced.add(i));
    if (this.lens === 'live' && this.live)
      this.live.nodes.forEach((i) => {
        if (m.byId.get(i)?.kind === 'Endpoint') this.forced.add(i);
      });
  }

  private view(): ViewState {
    return {
      lens: this.lens,
      focusId: this.focusId,
      selected: this.selected,
      lit: this.lit,
      expanded: this.expanded,
      pulsing: this.pulsing,
      liveNodes: this.lens === 'live' && this.live ? this.live.nodes : new Set(),
      pos: this.pos,
    };
  }

  private refresh(): void {
    const m = this.model;
    if (!m || !this.nodesDS || !this.edgesDS) return;
    this.computeForced();
    this.pos = layout(m, this.expanded, this.forced);
    const view = this.view();
    const nodes = m.nodes.filter((n) => this.isVisible(n.id));
    const ids = new Set(nodes.map((n) => n.id));
    this.nodesDS.remove([...this.shownNodes].filter((i) => !ids.has(i)));
    const styled = nodes.map((n) => nodeStyle(m, view, this.palette, n));
    this.sizes = new Map(styled.map((s) => [s['id'] as string, s['size'] as number]));
    this.nodesDS.update(styled);
    this.shownNodes = ids;
    const edges = edgeList(
      m,
      (id) => this.isVisible(id),
      (id) => this.repOf(id),
    ).map((e: EdgeItem) => edgeStyle(view, this.palette, e));
    const eids = new Set(edges.map((e) => e['id'] as string));
    this.edgesDS.remove([...this.shownEdges].filter((i) => !eids.has(i)));
    this.edgesDS.update(edges);
    this.shownEdges = eids;
    this.emit();
  }

  private emit(): void {
    const m = this.model!;
    const crumbs: string[] = [];
    if (this.focusId) crumbs.push(`Focused on ${m.shortName(m.byId.get(this.focusId)!)}`);
    if (this.lit.size) crumbs.push(`${this.lit.size} nodes lit`);
    const state: EngineState = {
      lens: this.lens,
      crumb:
        crumbs.join(' · ') ||
        `${this.shownNodes.size} of ${m.nodes.length} nodes shown · click a ring to open it · double-click to focus`,
      canBack: this.history.length > 0 || !!this.focusId || this.lit.size > 0,
      pulsing: [...this.pulsing],
    };
    this.changeListeners.forEach((cb) => cb(state));
  }

  private animation(): false | { duration: number; easingFunction: string } {
    return this.reduceMotion
      ? false
      : { duration: this.palette.camera, easingFunction: 'easeInOutQuad' };
  }

  private fit(nodes?: string[]): void {
    // vis-network refuses `nodes: undefined`; leaving it out fits everything.
    this.network?.fit(
      nodes ? { nodes, animation: this.animation() } : { animation: this.animation() },
    );
  }

  /** Redraws every frame while something moves: pulses (unless motion is reduced) or live requests. */
  private animate(): void {
    if (this.raf || !this.network) {
      this.network?.redraw();
      return;
    }
    const tick = (now: number) => {
      const moving =
        (this.pulsing.size > 0 && !this.reduceMotion) ||
        (this.lens === 'live' && !!this.live?.active);
      if (!moving || !this.network) {
        this.raf = 0;
        this.network?.redraw();
        return;
      }
      this.live?.advance(now);
      this.network.redraw();
      this.raf = requestAnimationFrame(tick);
    };
    this.raf = requestAnimationFrame(tick);
  }

  private drawOverlay(ctx: CanvasRenderingContext2D): void {
    if (!this.network || !this.model) return;
    const now = performance.now();
    if (this.lens === 'live' && this.live?.active) {
      const positions = this.network.getPositions([...this.shownNodes]);
      this.live.draw(
        ctx,
        (id) => {
          const rep = this.repOf(id);
          return rep ? positions[rep] : undefined;
        },
        this.palette,
        now,
        this.reduceMotion,
      );
    }
    if (this.pulsing.size) this.drawPulses(ctx, now);
  }

  /** A ring around each pulsing node that breathes out and back over two --dur-slow beats. */
  private drawPulses(ctx: CanvasRenderingContext2D, now: number): void {
    const positions = this.network!.getPositions(
      [...this.pulsing].filter((id) => this.shownNodes.has(id)),
    );
    const beat = this.palette.camera * 2;
    const t = this.reduceMotion ? 0 : (now % beat) / beat;
    const phase = t < 0.5 ? t * 2 : 2 - t * 2;
    ctx.save();
    ctx.strokeStyle = this.palette.accent;
    ctx.lineWidth = 2;
    for (const [id, p] of Object.entries(positions)) {
      const r = (this.sizes.get(id) ?? 5) + 6 + phase * 6;
      ctx.globalAlpha = 1 - phase * 0.6;
      ctx.beginPath();
      ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
      ctx.stroke();
    }
    ctx.restore();
  }
}
