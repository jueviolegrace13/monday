import {
  Component,
  ElementRef,
  OnDestroy,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MondayApi } from '../api/monday-api';
import { EngineState, GraphEngine } from './graph-engine';
import { LENSES, Lens } from './graph-screen';
import { EDGE_LABEL, EDGE_LINE, cssTokens, readPalette } from './graph-style';
import { VIS_LOADER } from './vis';

type Load = 'loading' | 'ready' | 'empty' | 'failed';

const LENS_LABEL: Record<Lens, string> = {
  structure: 'Structure',
  security: 'Threats',
  coverage: 'Coverage',
  live: 'Live',
};

/**
 * The code graph of one project, with the lens and navigation controls over it. Drawing and
 * interaction are the approved viewer's (GraphEngine); this component loads the data, keeps the
 * chrome in step with the engine and feeds the Live lens with real requests.
 */
@Component({
  selector: 'app-graph-canvas',
  templateUrl: './graph-canvas.component.html',
  styleUrl: './graph-canvas.component.css',
})
export class GraphCanvasComponent implements OnDestroy {
  private readonly api = inject(MondayApi);
  private readonly loadVis = inject(VIS_LOADER);

  readonly projectId = input.required<string>();
  /** The engine once a graph is drawn, and null when none is (no graph yet, or it failed to load). */
  readonly engineReady = output<GraphEngine | null>();
  readonly selected = output<string | null>();

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('graph');

  protected readonly load = signal<Load>('loading');
  protected readonly error = signal('');
  protected readonly state = signal<EngineState>({
    lens: 'structure',
    crumb: '',
    canBack: false,
    pulsing: [],
  });
  protected readonly liveUnavailable = signal(false);
  protected readonly lenses = LENSES;
  protected readonly lensLabel = LENS_LABEL;
  protected readonly legend = (Object.keys(EDGE_LINE) as (keyof typeof EDGE_LINE)[])
    .filter((k) => k !== 'Contains')
    .map((k) => ({ kind: k, label: EDGE_LABEL[k] ?? k, dashed: EDGE_LINE[k].dashes !== false }));

  private engine: GraphEngine | null = null;
  private liveAbort: AbortController | null = null;
  private loadAbort: AbortController | null = null;

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.open(id));
    });
  }

  ngOnDestroy(): void {
    this.loadAbort?.abort();
    this.stopLive();
    this.engine?.destroy();
  }

  protected async open(projectId: string): Promise<void> {
    this.loadAbort?.abort();
    const abort = new AbortController();
    this.loadAbort = abort;
    this.stopLive();
    this.load.set('loading');
    try {
      const [vis, graph] = await Promise.all([
        this.loadVis(),
        this.api.graph(projectId, abort.signal),
      ]);
      if (abort.signal.aborted) return;
      if (!graph || !graph.nodes.length) {
        this.drop();
        this.load.set('empty');
        return;
      }
      // A new engine per graph; the person's lens carries over to the next project.
      const lens = this.state().lens;
      this.drop();
      const reduce =
        typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
      const engine = new GraphEngine(
        vis,
        this.host().nativeElement,
        readPalette(cssTokens),
        reduce,
      );
      engine.onChange((s) => this.changed(s));
      engine.onSelect((id) => this.selected.emit(id));
      engine.chooseLens(lens);
      this.engine = engine;
      this.load.set('ready');
      engine.load(graph);
      this.engineReady.emit(engine);
      // The lens did not change, so changed() saw no transition: open this project's stream here.
      if (lens === 'live') void this.startLive();
    } catch (e) {
      if (abort.signal.aborted) return;
      this.drop();
      this.error.set(e instanceof Error ? e.message : 'The graph could not be loaded.');
      this.load.set('failed');
    }
  }

  /** Removes the drawn graph, so nothing of the previous project stays on screen or clickable. */
  private drop(): void {
    if (!this.engine) return;
    this.engine.destroy();
    this.engine = null;
    this.selected.emit(null);
    this.engineReady.emit(null);
    this.state.update((s) => ({ ...s, crumb: '', canBack: false, pulsing: [] }));
  }

  protected retry(): void {
    void this.open(this.projectId());
  }

  protected chooseLens(lens: Lens): void {
    this.engine?.chooseLens(lens);
  }

  protected back(): void {
    this.engine?.back();
  }

  protected home(): void {
    this.engine?.home();
  }

  protected openAll(): void {
    this.engine?.expandAll();
  }

  private changed(next: EngineState): void {
    const was = this.state().lens;
    this.state.set(next);
    if (next.lens === 'live' && was !== 'live') void this.startLive();
    else if (next.lens !== 'live' && was === 'live') this.stopLive();
  }

  /** The Live lens shows requests as they happen; the stream closes when the lens changes. */
  private async startLive(): Promise<void> {
    this.stopLive();
    const abort = new AbortController();
    this.liveAbort = abort;
    try {
      for await (const request of this.api.liveTraffic(this.projectId(), abort.signal))
        this.engine?.request(request);
    } catch {
      // Not allowed or not reachable: the lens still shows the request chains, and says so.
      if (!abort.signal.aborted) this.liveUnavailable.set(true);
    }
  }

  private stopLive(): void {
    this.liveAbort?.abort();
    this.liveAbort = null;
    this.liveUnavailable.set(false);
  }
}
