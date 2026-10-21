/** The graph's lenses, as the agent's set_lens tool names them. */
export type Lens = 'structure' | 'security' | 'coverage' | 'live';

export const LENSES: readonly Lens[] = ['structure', 'security', 'coverage', 'live'];

export const isLens = (value: unknown): value is Lens =>
  typeof value === 'string' && (LENSES as readonly string[]).includes(value);

/**
 * What the chat may do to the graph on screen. The graph engine implements it; the chat store only
 * knows this port, so it can be tested without a canvas. Every call that changes the view is
 * remembered, so Back or Esc undoes it; pulse and settle are decoration and are not.
 */
export interface GraphScreen {
  focus(nodeId: string): void;
  highlightPath(nodeIds: string[]): void;
  setLens(lens: Lens): void;
  /** Shows a replay the server already ran: the Live lens with the nodes its requests reached lit. */
  replay(nodeIds: string[]): void;
  /** Nodes a tool touched pulse until settle. */
  pulse(nodeIds: string[]): void;
  settle(): void;
  back(): void;
}
