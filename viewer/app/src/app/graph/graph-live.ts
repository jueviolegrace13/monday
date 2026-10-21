import { TrafficView } from '../api/monday-api';
import { Point } from './graph-layout';
import { Chain, GraphModel } from './graph-model';
import { Palette } from './graph-style';

// The Live lens: each real request walks the chain the Request path shows (endpoint → middleware →
// guards → handler → calls) and stops where it really stopped. Carried over from the recorded-replay
// mode of viewer/artifact/index.html; the viewer's simulated traffic is not, since the app shows real
// requests only. Request bodies, users and values never reach this code: only route, status, outcome.

interface Moving {
  epId: string;
  chain: string[];
  stopAt: number;
  failed: boolean;
  hop: number;
  hopStart: number;
  hopMs: number;
  end?: number;
}

/** How long a finished request's red or green flash stays: the artifact's value. */
const FLASH_MS = 650;

export class LiveRequests {
  private readonly chains = new Map<string, Chain>();
  private reqs: Moving[] = [];
  /** Every node any endpoint's chain passes, so the lens can keep them lit. */
  readonly nodes = new Set<string>();

  constructor(private readonly model: GraphModel) {
    for (const ep of model.nodes.filter((n) => n.kind === 'Endpoint')) {
      const chain = model.chainFor(ep);
      this.chains.set(ep.id, chain);
      chain.ids.forEach((id) => this.nodes.add(id));
    }
  }

  get active(): boolean {
    return this.reqs.length > 0;
  }

  /** A request that just happened starts moving now. */
  add(view: TrafficView, now: number): void {
    const ep =
      this.model.byId.get(view.nodeId) ??
      this.model.byId.get(`endpoint:${view.method} ${view.route}`);
    const chain = ep ? this.chains.get(ep.id) : undefined;
    if (!ep || !chain) return;
    const endAt = chain.ids.indexOf(ep.id);
    const stopAt =
      view.outcome === 'Unauthorized'
        ? chain.gate
        : view.outcome === 'NotFound' ||
            view.outcome === 'Invalid' ||
            view.outcome === 'RateLimited'
          ? endAt
          : chain.ids.length - 1;
    const ms = Math.max(1, view.durationMs);
    this.reqs.push({
      epId: ep.id,
      chain: chain.ids,
      stopAt: Math.max(0, stopAt),
      failed: view.status >= 400,
      hop: 0,
      hopStart: now,
      hopMs: 110 + Math.min(220, ms * 4),
    });
  }

  advance(now: number): void {
    for (const r of this.reqs) {
      if (r.end) continue;
      if (now - r.hopStart >= r.hopMs) {
        if (r.hop >= r.stopAt) r.end = now;
        else {
          r.hop++;
          r.hopStart = now;
          r.hopMs = 120 + Math.random() * 200;
        }
      }
    }
    this.reqs = this.reqs.filter((r) => !r.end || now - r.end < FLASH_MS);
  }

  clear(): void {
    this.reqs = [];
  }

  /** Draws on the network's canvas; `at` gives the canvas position of the visible node holding an id. */
  draw(
    ctx: CanvasRenderingContext2D,
    at: (id: string) => Point | undefined,
    p: Palette,
    now: number,
    reduceMotion: boolean,
  ): void {
    const counts = new Map<string, number>();
    for (const r of this.reqs) {
      if (!r.end) counts.set(r.epId, (counts.get(r.epId) ?? 0) + 1);
      if (reduceMotion) continue;
      const a = at(r.chain[r.hop]);
      const b = at(r.chain[Math.min(r.hop + 1, r.stopAt)]);
      if (!a || !b) continue;
      const t = r.end ? 1 : Math.min(1, (now - r.hopStart) / r.hopMs);
      const x = a.x + (b.x - a.x) * t;
      const y = a.y + (b.y - a.y) * t;
      const color = r.end ? (r.failed ? p.bad : p.ok) : p.warn;
      const radius = r.end ? 4 + (now - r.end) / 60 : 4;
      ctx.globalAlpha = r.end ? Math.max(0, 1 - (now - r.end) / FLASH_MS) : 1;
      ctx.beginPath();
      ctx.arc(x, y, radius, 0, Math.PI * 2);
      ctx.fillStyle = color;
      ctx.shadowColor = color;
      ctx.shadowBlur = 12;
      ctx.fill();
      ctx.shadowBlur = 0;
      ctx.globalAlpha = 1;
    }
    // In-flight count per endpoint: a number, so it reads without colour.
    ctx.font = `${p.weightStrong} ${p.label.badge}px ${p.fontMono}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const [id, n] of counts) {
      const pt = at(id);
      if (!pt) continue;
      const bx = pt.x + 16;
      const by = pt.y - 16;
      ctx.beginPath();
      ctx.arc(bx, by, 9, 0, Math.PI * 2);
      ctx.fillStyle = p.warn;
      ctx.fill();
      ctx.fillStyle = p.bg;
      ctx.fillText(String(n), bx, by + 0.5);
    }
  }
}
