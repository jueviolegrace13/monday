import { InjectionToken } from '@angular/core';

// vis-network draws the graph, as it does in the approved viewer (viewer/artifact/index.html): the
// same pinned build from the same CDN, now with a subresource-integrity hash so a changed file is
// refused. Only the parts of its API the engine uses are typed here.

export const VIS_NETWORK_URL =
  'https://unpkg.com/vis-network@9.1.6/standalone/umd/vis-network.min.js';
export const VIS_NETWORK_INTEGRITY =
  'sha384-Ux6phic9PEHJ38YtrijhkzyJ8yQlH8i/+buBR8s3mAZOJrP1gwyvAcIYl3GWtpX1';

export interface VisDataSet<T> {
  update(items: T[]): void;
  remove(ids: string[]): void;
}

export interface VisClick {
  nodes: string[];
  edges: string[];
}

export interface VisNetwork {
  on(event: 'click' | 'doubleClick', cb: (p: VisClick) => void): void;
  on(event: 'afterDrawing', cb: (ctx: CanvasRenderingContext2D) => void): void;
  fit(options?: {
    nodes?: string[];
    animation?: false | { duration: number; easingFunction?: string };
  }): void;
  focus(
    id: string,
    options?: { scale?: number; animation?: false | { duration: number; easingFunction?: string } },
  ): void;
  selectNodes(ids: string[]): void;
  unselectAll(): void;
  redraw(): void;
  getPositions(ids?: string[]): Record<string, { x: number; y: number }>;
  setSize(width: string, height: string): void;
  destroy(): void;
}

export interface VisLib {
  DataSet: new <T>(items: T[]) => VisDataSet<T>;
  Network: new (container: HTMLElement, data: unknown, options: unknown) => VisNetwork;
}

export type VisLoader = () => Promise<VisLib>;

let loading: Promise<VisLib> | null = null;

/** Loads vis-network once per page. */
export const loadVis: VisLoader = () => {
  const ready = (window as unknown as { vis?: VisLib }).vis;
  if (ready) return Promise.resolve(ready);
  loading ??= new Promise<VisLib>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = VIS_NETWORK_URL;
    script.integrity = VIS_NETWORK_INTEGRITY;
    script.crossOrigin = 'anonymous';
    script.onload = () => {
      const vis = (window as unknown as { vis?: VisLib }).vis;
      if (vis) resolve(vis);
      else reject(new Error('The graph library did not load.'));
    };
    script.onerror = () => {
      loading = null;
      reject(new Error('The graph library could not be loaded.'));
    };
    document.head.appendChild(script);
  });
  return loading;
};

export const VIS_LOADER = new InjectionToken<VisLoader>('VIS_LOADER', { factory: () => loadVis });
