import { signal } from '@angular/core';
import { AgentEvent, ApiError, MondayApi } from '../api/monday-api';
import { GraphScreen, isLens } from '../graph/graph-screen';

/** One tool the agent ran for an answer, shown as a compact line: `traffic_stats · 3 nodes`. */
export interface ChatStep {
  name: string;
  nodeIds: string[];
  isError: boolean;
}

export type MessageState = 'streaming' | 'done' | 'stopped' | 'error';

export interface ChatMessage {
  id: number;
  role: 'user' | 'assistant';
  text: string;
  steps: ChatStep[];
  state: MessageState;
  error?: { code: string; message: string };
}

/** What the store reads from the screen around it each time a message is sent. */
export interface ChatContext {
  projectId(): string;
  selectedNodeId(): string | null;
  /** Debuggers and up: screen actions run. Monitors are ask-only, so actions are ignored. */
  canAct(): boolean;
}

const NOT_CONFIGURED = 'model.not_configured';

/**
 * One conversation with the agent. Streams the answer into `messages`, pulses the nodes each tool
 * touches, runs screen actions on the graph, and settles the graph when the answer ends. Stop at any
 * moment (before the response, mid-stream) is a clean stop: what arrived is kept, no error.
 */
export class ChatStore {
  readonly messages = signal<ChatMessage[]>([]);
  readonly streaming = signal(false);
  /** The last answer was refused for want of a model; the panel says so and links to settings. Cleared when an answer starts. */
  readonly notConfigured = signal(false);

  private conversationId: string | undefined;
  private abort: AbortController | null = null;
  private nextId = 1;
  /** Nodes of the last tool step, for the action that follows it (start_replay carries none). */
  private lastStepNodes: string[] = [];

  constructor(
    private readonly api: Pick<MondayApi, 'chat'>,
    private readonly screen: GraphScreen,
    private readonly context: ChatContext,
  ) {}

  async send(text: string): Promise<void> {
    const message = text.trim();
    if (!message || this.streaming()) return;

    const abort = new AbortController();
    this.abort = abort;
    this.lastStepNodes = [];
    this.streaming.set(true);
    this.append({ role: 'user', text: message, steps: [], state: 'done' });
    const answer = this.append({ role: 'assistant', text: '', steps: [], state: 'streaming' });

    const selected = this.context.selectedNodeId();
    try {
      const events = this.api.chat(
        this.context.projectId(),
        {
          message,
          ...(this.conversationId ? { conversationId: this.conversationId } : {}),
          ...(selected ? { selectedNodeId: selected } : {}),
        },
        abort.signal,
      );
      for await (const event of events) {
        if (abort.signal.aborted) break;
        this.apply(answer, event);
      }
      this.finish(answer, abort.signal.aborted ? 'stopped' : 'done');
    } catch (error) {
      if (abort.signal.aborted || isAbort(error)) this.finish(answer, 'stopped');
      else
        this.fail(answer, {
          code: error instanceof ApiError ? (error.code ?? `http.${error.status}`) : 'network',
          message: error instanceof Error ? error.message : 'The answer could not be loaded.',
        });
    } finally {
      if (this.abort === abort) this.abort = null;
      this.streaming.set(false);
      this.screen.settle();
    }
  }

  /** Ends the answer now. The server stops the loop when the stream closes. */
  stop(): void {
    this.abort?.abort();
  }

  /** A new conversation (another project, or the person asked for one). */
  reset(): void {
    this.stop();
    this.conversationId = undefined;
    this.messages.set([]);
    this.notConfigured.set(false);
  }

  private apply(answer: number, event: AgentEvent): void {
    switch (event.event) {
      case 'started':
        this.conversationId = event.conversationId;
        this.notConfigured.set(false);
        break;
      case 'text':
        this.update(answer, (m) => ({ ...m, text: m.text + event.text }));
        break;
      case 'tool': {
        const step = { name: event.name, nodeIds: [...event.nodeIds], isError: event.isError };
        this.lastStepNodes = step.nodeIds;
        this.update(answer, (m) => ({ ...m, steps: [...m.steps, step] }));
        if (step.nodeIds.length) this.screen.pulse(step.nodeIds);
        break;
      }
      case 'action':
        if (this.context.canAct()) this.act(event.type, event.args);
        break;
      case 'done':
        this.conversationId = event.conversationId;
        break;
      case 'error':
        if (event.code === NOT_CONFIGURED) this.notConfigured.set(true);
        this.fail(answer, { code: event.code, message: event.message });
        break;
    }
  }

  /** Screen actions run as they arrive; each is remembered by the graph, so Back or Esc undoes it. */
  private act(type: string, args: Record<string, unknown>): void {
    switch (type) {
      case 'focus_node':
        if (typeof args['nodeId'] === 'string') this.screen.focus(args['nodeId']);
        break;
      case 'highlight_path':
        if (isStringList(args['nodeIds'])) this.screen.highlightPath(args['nodeIds']);
        break;
      case 'set_lens':
        if (isLens(args['lens'])) this.screen.setLens(args['lens']);
        break;
      case 'start_replay':
        // The server ran the replay (and audited a user replay) when the tool ran. Asking the replay
        // API again would repeat that, so the viewer shows the nodes the tool step reported.
        this.screen.replay(this.lastStepNodes);
        break;
    }
  }

  private finish(answer: number, state: 'done' | 'stopped'): void {
    this.update(answer, (m) => (m.state === 'streaming' ? { ...m, state } : m));
  }

  private fail(answer: number, error: { code: string; message: string }): void {
    this.update(answer, (m) => ({ ...m, state: 'error', error }));
  }

  private append(message: Omit<ChatMessage, 'id'>): number {
    const id = this.nextId++;
    this.messages.update((list) => [...list, { ...message, id }]);
    return id;
  }

  private update(id: number, change: (m: ChatMessage) => ChatMessage): void {
    this.messages.update((list) => list.map((m) => (m.id === id ? change(m) : m)));
  }
}

const isAbort = (error: unknown): boolean =>
  error instanceof DOMException
    ? error.name === 'AbortError'
    : (error as Error)?.name === 'AbortError';

const isStringList = (value: unknown): value is string[] =>
  Array.isArray(value) && value.every((v) => typeof v === 'string');
