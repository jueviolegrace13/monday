import { GraphEngine } from './graph-engine';
import { GraphScreen, Lens } from './graph-screen';

/**
 * The screen the chat acts on, before and after the graph is drawn. Calls made while no graph is on
 * screen (still loading, no graph yet) do nothing.
 */
export class GraphScreenRef implements GraphScreen {
  engine: GraphEngine | null = null;

  focus(nodeId: string): void {
    this.engine?.focus(nodeId);
  }
  highlightPath(nodeIds: string[]): void {
    this.engine?.highlightPath(nodeIds);
  }
  setLens(lens: Lens): void {
    this.engine?.setLens(lens);
  }
  replay(nodeIds: string[]): void {
    this.engine?.replay(nodeIds);
  }
  pulse(nodeIds: string[]): void {
    this.engine?.pulse(nodeIds);
  }
  settle(): void {
    this.engine?.settle();
  }
  back(): void {
    this.engine?.back();
  }
}
