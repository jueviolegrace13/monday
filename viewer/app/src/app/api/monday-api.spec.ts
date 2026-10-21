import { AgentEvent, ApiError, MondayApi } from './monday-api';

const enc = new TextEncoder();

/** A response body that delivers the given chunks one by one, like a network would. */
function sseResponse(chunks: string[], status = 200): Response {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of chunks) controller.enqueue(enc.encode(c));
      controller.close();
    },
  });
  return new Response(stream, { status, headers: { 'Content-Type': 'text/event-stream' } });
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

async function collect<T>(it: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const v of it) out.push(v);
  return out;
}

const PROJECT = '11111111-1111-1111-1111-111111111111';
const CONVERSATION = '22222222-2222-2222-2222-222222222222';

describe('MondayApi.chat', () => {
  function apiWith(response: Response | (() => Response)) {
    const calls: { url: string; init: RequestInit }[] = [];
    const api = new MondayApi({
      devUser: 'dev',
      fetch: async (url, init) => {
        calls.push({ url: String(url), init: init ?? {} });
        return typeof response === 'function' ? response() : response;
      },
    });
    return { api, calls };
  }

  it('parsesSseIntoAgentEvents', async () => {
    const { api } = apiWith(
      sseResponse([
        `event: started\ndata: {"conversationId":"${CONVERSATION}"}\n\n`,
        'event: text\ndata: {"text":"Hello"}\n\n',
        'event: tool\ndata: {"name":"get_node","nodeIds":["Monday.Api.Program"],"isError":false}\n\n',
        'event: action\ndata: {"type":"select_node","args":{"nodeId":"a"}}\n\n',
        `event: done\ndata: {"conversationId":"${CONVERSATION}","usage":{"inputTokens":10,"outputTokens":4}}\n\n`,
      ]),
    );

    const events = await collect(api.chat(PROJECT, { message: 'hi' }));

    expect(events).toEqual<AgentEvent[]>([
      { event: 'started', conversationId: CONVERSATION },
      { event: 'text', text: 'Hello' },
      { event: 'tool', name: 'get_node', nodeIds: ['Monday.Api.Program'], isError: false },
      { event: 'action', type: 'select_node', args: { nodeId: 'a' } },
      { event: 'done', conversationId: CONVERSATION, usage: { inputTokens: 10, outputTokens: 4 } },
    ]);
  });

  it('posts the question as camelCase json with the dev user header', async () => {
    const { api, calls } = apiWith(sseResponse([]));
    await collect(
      api.chat(PROJECT, { message: 'hi', conversationId: CONVERSATION, selectedNodeId: 'n1' }),
    );

    expect(calls[0].url).toBe(`/projects/${PROJECT}/chat`);
    expect(calls[0].init.method).toBe('POST');
    expect(JSON.parse(String(calls[0].init.body))).toEqual({
      message: 'hi',
      conversationId: CONVERSATION,
      selectedNodeId: 'n1',
    });
    const headers = new Headers(calls[0].init.headers);
    expect(headers.get('X-Monday-Dev-User')).toBe('dev');
    expect(headers.get('Content-Type')).toBe('application/json');
    expect(headers.get('Accept')).toBe('text/event-stream');
  });

  it('handles frames split across chunks, CRLF, comments, multi-line data and the UTF-8 split of a character', async () => {
    const bytes = enc.encode('event: text\r\ndata: {"text":"héllo"}\r\n\r\n');
    const cut = bytes.indexOf(0xc3) + 1; // between the two bytes of "é"
    const stream = new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(enc.encode(': keep-alive\n\n'));
        c.enqueue(bytes.slice(0, cut));
        c.enqueue(bytes.slice(cut));
        c.enqueue(enc.encode('event: text\ndata: {"text":\ndata: "two lines"}\n\n'));
        c.close();
      },
    });
    const { api } = apiWith(new Response(stream));

    const events = await collect(api.chat(PROJECT, { message: 'x' }));

    expect(events).toEqual([
      { event: 'text', text: 'héllo' },
      { event: 'text', text: 'two lines' },
    ]);
  });

  it('delivers a final frame that has no blank line after it, and ignores unknown events and bad json', async () => {
    const { api } = apiWith(
      sseResponse([
        'event: ping\ndata: {}\n\n',
        'event: text\ndata: not json\n\n',
        'event: text\ndata: {"text":"end"}',
      ]),
    );

    expect(await collect(api.chat(PROJECT, { message: 'x' }))).toEqual([
      { event: 'text', text: 'end' },
    ]);
  });

  it('turns a refusal before the stream into an ApiError with the problem details', async () => {
    const { api } = apiWith(
      json({ title: 'Forbidden', detail: 'You cannot ask here.', code: 'permission.denied' }, 403),
    );

    const error = await collect(api.chat(PROJECT, { message: 'x' })).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(403);
    expect((error as ApiError).message).toBe('You cannot ask here.');
    expect((error as ApiError).code).toBe('permission.denied');
  });

  it('stops when the signal aborts, and cancels the stream', async () => {
    let cancelled = false;
    const stream = new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(enc.encode('event: text\ndata: {"text":"one"}\n\n'));
      },
      cancel() {
        cancelled = true;
      },
    });
    const { api } = apiWith(new Response(stream));
    const abort = new AbortController();

    const seen: AgentEvent[] = [];
    for await (const e of api.chat(PROJECT, { message: 'x' }, abort.signal)) {
      seen.push(e);
      abort.abort();
    }

    expect(seen).toEqual([{ event: 'text', text: 'one' }]);
    expect(cancelled).toBe(true);
  });

  it('passes the signal to fetch', async () => {
    const { api, calls } = apiWith(sseResponse([]));
    const abort = new AbortController();
    await collect(api.chat(PROJECT, { message: 'x' }, abort.signal));
    expect(calls[0].init.signal).toBe(abort.signal);
  });
});

describe('MondayApi requests', () => {
  it('lists projects', async () => {
    const projects = [{ id: PROJECT, name: 'Monday', role: 'Owner', environments: [] }];
    const calls: string[] = [];
    const api = new MondayApi({ fetch: async (url) => (calls.push(String(url)), json(projects)) });

    expect(await api.projects()).toEqual(projects);
    expect(calls).toEqual(['/projects/']);
  });

  it('returns null for a project with no graph yet (204)', async () => {
    const api = new MondayApi({ fetch: async () => new Response(null, { status: 204 }) });
    expect(await api.graph(PROJECT)).toBeNull();
  });

  it('encodes node ids and activity ranges in the query', async () => {
    const calls: string[] = [];
    const api = new MondayApi({ fetch: async (url) => (calls.push(String(url)), json({})) });

    await api.node(PROJECT, 'Monday.Api.Program.Main(string[])');
    await api.activity(PROJECT, new Date('2026-10-21T00:00:00Z'), new Date('2026-10-22T00:00:00Z'));

    expect(calls[0]).toBe(
      `/projects/${PROJECT}/graph/node?nodeId=Monday.Api.Program.Main(string%5B%5D)`,
    );
    expect(calls[1]).toBe(
      `/projects/${PROJECT}/activity?from=2026-10-21T00%3A00%3A00.000Z&to=2026-10-22T00%3A00%3A00.000Z`,
    );
  });

  it('streams live traffic as it arrives', async () => {
    const view = { traceId: 't1', method: 'GET', route: '/health', status: 200, durationMs: 1.5 };
    const api = new MondayApi({
      fetch: async () =>
        sseResponse([': connected\n\n', `event: request\ndata: ${JSON.stringify(view)}\n\n`]),
    });

    expect(await collect(api.liveTraffic(PROJECT))).toEqual([view]);
  });

  it('reports a non-json failure by its status', async () => {
    const api = new MondayApi({ fetch: async () => new Response('boom', { status: 502 }) });
    await expect(api.projects()).rejects.toMatchObject({ status: 502 });
  });
});
