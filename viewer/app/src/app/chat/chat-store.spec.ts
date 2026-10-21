import { AgentEvent, ApiError, ChatInput } from '../api/monday-api';
import { GraphScreen, Lens } from '../graph/graph-screen';
import { ChatStore } from './chat-store';

const PROJECT = '11111111-1111-1111-1111-111111111111';
const CONVERSATION = '22222222-2222-2222-2222-222222222222';

/** Records every call the store makes on the graph. */
class FakeScreen implements GraphScreen {
  readonly calls: string[] = [];
  focus(nodeId: string): void {
    this.calls.push(`focus ${nodeId}`);
  }
  highlightPath(nodeIds: string[]): void {
    this.calls.push(`highlightPath ${nodeIds.join(',')}`);
  }
  setLens(lens: Lens): void {
    this.calls.push(`setLens ${lens}`);
  }
  replay(nodeIds: string[]): void {
    this.calls.push(`replay ${nodeIds.join(',')}`);
  }
  pulse(nodeIds: string[]): void {
    this.calls.push(`pulse ${nodeIds.join(',')}`);
  }
  settle(): void {
    this.calls.push('settle');
  }
  back(): void {
    this.calls.push('back');
  }
}

/** A chat stream the test drives: push events, then end it, or let the store abort it. */
class ScriptedChat {
  readonly inputs: ChatInput[] = [];
  readonly signals: AbortSignal[] = [];
  private queue: AgentEvent[] = [];
  private ended = false;
  private wake: (() => void) | null = null;

  push(...events: AgentEvent[]): void {
    this.queue.push(...events);
    this.wake?.();
  }

  end(): void {
    this.ended = true;
    this.wake?.();
  }

  /** Lets the next call stream again after an earlier one ended. */
  reopen(): void {
    this.ended = false;
  }

  readonly chat = (projectId: string, input: ChatInput, signal?: AbortSignal) => {
    this.inputs.push(input);
    if (signal) this.signals.push(signal);
    const self = this;
    return (async function* () {
      expect(projectId).toBe(PROJECT);
      for (;;) {
        if (signal?.aborted) return;
        const next = self.queue.shift();
        if (next) {
          yield next;
          continue;
        }
        if (self.ended) return;
        await new Promise<void>((resolve) => {
          self.wake = resolve;
          signal?.addEventListener('abort', () => resolve(), { once: true });
        });
        self.wake = null;
      }
    })();
  };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

function setup(options: { selected?: string | null; canAct?: boolean } = {}) {
  const screen = new FakeScreen();
  const script = new ScriptedChat();
  const store = new ChatStore({ chat: script.chat }, screen, {
    projectId: () => PROJECT,
    selectedNodeId: () => options.selected ?? null,
    canAct: () => options.canAct ?? true,
  });
  return { screen, script, store };
}

describe('ChatStore', () => {
  it('toolEventPulsesNodes', async () => {
    const { screen, script, store } = setup();
    const sending = store.send('Which endpoints are slow?');
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'tool', name: 'traffic_stats', nodeIds: ['a', 'b', 'c'], isError: false },
    );
    await tick();

    expect(screen.calls).toContain('pulse a,b,c');
    const answer = store.messages().at(-1)!;
    expect(answer.role).toBe('assistant');
    expect(answer.steps).toEqual([
      { name: 'traffic_stats', nodeIds: ['a', 'b', 'c'], isError: false },
    ]);
    expect(store.streaming()).toBe(true);

    script.push(
      { event: 'text', text: 'Two are.' },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 1, outputTokens: 1 } },
    );
    script.end();
    await sending;

    expect(screen.calls.at(-1)).toBe('settle');
    expect(store.streaming()).toBe(false);
    expect(store.messages().at(-1)!.text).toBe('Two are.');
    expect(store.messages().at(-1)!.state).toBe('done');
  });

  it('actionEventCallsEngine', async () => {
    const { screen, script, store } = setup();
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'tool', name: 'focus_node', nodeIds: ['n1'], isError: false },
      { event: 'action', type: 'focus_node', args: { nodeId: 'n1' } },
      { event: 'tool', name: 'highlight_path', nodeIds: ['n1', 'n2'], isError: false },
      { event: 'action', type: 'highlight_path', args: { nodeIds: ['n1', 'n2'] } },
      { event: 'tool', name: 'set_lens', nodeIds: [], isError: false },
      { event: 'action', type: 'set_lens', args: { lens: 'security' } },
      { event: 'tool', name: 'start_replay', nodeIds: ['e1', 'e2'], isError: false },
      {
        event: 'action',
        type: 'start_replay',
        args: { since: '2026-10-21T00:00:00Z', until: '2026-10-21T01:00:00Z', userId: 'u1' },
      },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 1, outputTokens: 1 } },
    );
    script.end();
    await store.send('Show me');

    expect(screen.calls).toEqual([
      'pulse n1',
      'focus n1',
      'pulse n1,n2',
      'highlightPath n1,n2',
      'setLens security',
      'pulse e1,e2',
      // The server already ran (and audited) the replay: the viewer only shows the nodes it reached.
      'replay e1,e2',
      'settle',
    ]);
  });

  it('ignores screen actions for a role that cannot act', async () => {
    const { screen, script, store } = setup({ canAct: false });
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'action', type: 'set_lens', args: { lens: 'live' } },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 1, outputTokens: 1 } },
    );
    script.end();
    await store.send('Show me');

    expect(screen.calls).toEqual(['settle']);
  });

  it('stopAbortsStream', async () => {
    const { screen, script, store } = setup();
    const sending = store.send('Long question');
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'tool', name: 'traffic_stats', nodeIds: ['a'], isError: false },
      { event: 'text', text: 'Partial' },
    );
    await tick();

    store.stop();
    await sending;

    expect(script.signals[0].aborted).toBe(true);
    expect(store.streaming()).toBe(false);
    const answer = store.messages().at(-1)!;
    expect(answer.state).toBe('stopped');
    expect(answer.text).toBe('Partial');
    expect(answer.error).toBeUndefined();
    expect(screen.calls.at(-1)).toBe('settle');
  });

  it('treats Stop before the response arrives as a clean stop', async () => {
    const screen = new FakeScreen();
    const store = new ChatStore(
      {
        chat: (_p, _i, signal) =>
          (async function* (): AsyncGenerator<AgentEvent> {
            // fetch rejects with AbortError when Stop comes before the headers.
            await new Promise((_, reject) =>
              signal!.addEventListener('abort', () =>
                reject(new DOMException('The operation was aborted.', 'AbortError')),
              ),
            );
          })(),
      },
      screen,
      { projectId: () => PROJECT, selectedNodeId: () => null, canAct: () => true },
    );

    const sending = store.send('Quick');
    store.stop();
    await sending;

    expect(store.streaming()).toBe(false);
    expect(store.messages().at(-1)!.state).toBe('stopped');
    expect(store.messages().at(-1)!.error).toBeUndefined();
  });

  it('selectedNodeSentWithMessage', async () => {
    const { script, store } = setup({ selected: 'method:Monday.Api.Program.Main' });
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 1, outputTokens: 1 } },
    );
    script.end();
    await store.send('  What does this do?  ');

    expect(script.inputs[0]).toEqual({
      message: 'What does this do?',
      selectedNodeId: 'method:Monday.Api.Program.Main',
    });
  });

  it('continues the conversation it started', async () => {
    const { script, store } = setup();
    script.push(
      { event: 'started', conversationId: CONVERSATION },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 1, outputTokens: 1 } },
    );
    script.end();
    await store.send('First');

    script.reopen();
    script.end();
    await store.send('Second');

    expect(script.inputs[0].conversationId).toBeUndefined();
    expect(script.inputs[1].conversationId).toBe(CONVERSATION);
  });

  it('shows a missing model as not configured', async () => {
    const { script, store } = setup();
    script.push({
      event: 'error',
      code: 'model.not_configured',
      message: 'No model is set up for this project yet.',
    });
    script.end();
    await store.send('Hello');

    expect(store.notConfigured()).toBe(true);
    expect(store.messages().at(-1)!.state).toBe('error');
    expect(store.messages().at(-1)!.error?.code).toBe('model.not_configured');
  });

  it('shows a refusal before the stream as an error on the answer', async () => {
    const screen = new FakeScreen();
    const store = new ChatStore(
      {
        chat: () =>
          (async function* (): AsyncGenerator<AgentEvent> {
            throw new ApiError(429, 'Too many questions; wait a minute.', 'rate_limited');
          })(),
      },
      screen,
      { projectId: () => PROJECT, selectedNodeId: () => null, canAct: () => true },
    );

    await store.send('Hello');

    expect(store.streaming()).toBe(false);
    expect(store.messages().at(-1)!.error).toEqual({
      code: 'rate_limited',
      message: 'Too many questions; wait a minute.',
    });
  });

  it('ignores empty messages and a second send while streaming', async () => {
    const { script, store } = setup();
    await store.send('   ');
    expect(store.messages()).toEqual([]);

    const first = store.send('One');
    await store.send('Two');
    expect(script.inputs.length).toBe(1);
    script.end();
    await first;
  });
});
