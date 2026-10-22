import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { GraphView, MondayApi, TrafficView } from '../api/monday-api';
import { GraphCanvasComponent } from './graph-canvas.component';
import { GraphEngine } from './graph-engine';
import { Lens } from './graph-screen';
import { VIS_LOADER, VisLib } from './vis';

/** Enough of vis-network to draw nothing: records whether each network was destroyed. */
const networks: { destroyed: boolean }[] = [];
const fakeVis: VisLib = {
  DataSet: class {
    update(): void {}
    remove(): void {}
  } as unknown as VisLib['DataSet'],
  Network: class {
    readonly record = { destroyed: false };
    constructor() {
      networks.push(this.record);
    }
    on(): void {}
    fit(): void {}
    focus(): void {}
    selectNodes(): void {}
    unselectAll(): void {}
    redraw(): void {}
    getPositions() {
      return {};
    }
    setSize(): void {}
    destroy(): void {
      this.record.destroyed = true;
    }
  } as unknown as VisLib['Network'],
};

const graph: GraphView = {
  commit: 'abc',
  indexedAt: '2026-10-21T00:00:00Z',
  nodes: [{ id: 'app', kind: 'App', name: 'Monday', guards: [] }],
  edges: [],
  findings: [],
  findingCounts: {},
};

@Component({
  imports: [GraphCanvasComponent],
  template: `@if (shown()) {
    <app-graph-canvas
      [projectId]="project()"
      [lens]="lens()"
      (lensChange)="lens.set($event)"
      (engineReady)="engine = $event"
    />
  }`,
})
class Host {
  readonly project = signal('a');
  readonly shown = signal(true);
  readonly lens = signal<Lens>('structure');
  engine: GraphEngine | null = null;
}

async function setup(
  graphs: Record<string, GraphView | null | Error>,
  live: (id: string) => void = () => {},
) {
  networks.length = 0;
  const liveCalls: string[] = [];
  await TestBed.configureTestingModule({
    imports: [Host],
    providers: [
      {
        provide: MondayApi,
        useValue: {
          graph: async (id: string) => {
            const g = graphs[id];
            if (g instanceof Error) throw g;
            return g;
          },
          liveTraffic: (id: string, signal: AbortSignal) =>
            (async function* (): AsyncGenerator<TrafficView> {
              liveCalls.push(id);
              live(id);
              await new Promise((r) => signal.addEventListener('abort', r));
            })(),
        },
      },
      { provide: VIS_LOADER, useValue: async () => fakeVis },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(Host);
  const settle = async () => {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((r) => setTimeout(r));
    fixture.detectChanges();
  };
  await settle();
  return { fixture, host: fixture.componentInstance, liveCalls, settle };
}

describe('GraphCanvasComponent', () => {
  it('keeps the Live lens streaming after a project switch', async () => {
    const { host, liveCalls, settle } = await setup({ a: graph, b: graph });
    host.engine!.chooseLens('live');
    await settle();
    expect(liveCalls).toEqual(['a']);

    host.project.set('b');
    await settle();

    expect(liveCalls).toEqual(['a', 'b']);
    expect(host.engine).not.toBeNull();
  });

  it('opens a new canvas in the lens the person last chose (back from a settings screen)', async () => {
    const { host, liveCalls, settle } = await setup({ a: graph });
    host.engine!.chooseLens('live');
    await settle();
    expect(host.lens()).toBe('live');

    host.shown.set(false);
    await settle();
    host.shown.set(true);
    await settle();

    expect(liveCalls).toEqual(['a', 'a']);
    expect(host.lens()).toBe('live');
  });

  it('clears the previous graph when the next project has none', async () => {
    const { host, settle, fixture } = await setup({ a: graph, b: null });
    expect(host.engine).not.toBeNull();

    host.project.set('b');
    await settle();

    expect(host.engine).toBeNull();
    expect(networks[0].destroyed).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No graph yet');
  });

  it('hands back no engine once the canvas is gone (another screen replaced it)', async () => {
    const { host, settle } = await setup({ a: graph });
    expect(host.engine).not.toBeNull();

    host.shown.set(false);
    await settle();

    expect(host.engine).toBeNull();
    expect(networks[0].destroyed).toBe(true);
  });

  it('clears the previous graph when the next load fails', async () => {
    const { host, settle } = await setup({ a: graph, b: new Error('Forbidden') });

    host.project.set('b');
    await settle();

    expect(host.engine).toBeNull();
    expect(networks[0].destroyed).toBe(true);
  });

  it('says so when live traffic is unavailable', async () => {
    const { host, settle, fixture } = await setup({ a: graph }, () => {
      throw new Error('Forbidden');
    });
    host.engine!.chooseLens('live');
    await settle();

    expect((fixture.nativeElement as HTMLElement).querySelector('.crumb')?.textContent).toContain(
      'live traffic unavailable',
    );
  });
});
