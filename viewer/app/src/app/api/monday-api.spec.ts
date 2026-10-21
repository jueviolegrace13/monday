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

  it('ends quietly when Stop comes before the response headers', async () => {
    const abort = new AbortController();
    const api = new MondayApi({
      fetch: (_url, init) =>
        new Promise<Response>((_, reject) =>
          init!.signal!.addEventListener('abort', () =>
            reject(new DOMException('The operation was aborted.', 'AbortError')),
          ),
        ),
    });

    const chat = collect(api.chat(PROJECT, { message: 'x' }, abort.signal));
    const live = collect(api.liveTraffic(PROJECT, abort.signal));
    abort.abort();

    expect(await chat).toEqual([]);
    expect(await live).toEqual([]);
  });

  it('still throws a network failure that was not a Stop', async () => {
    const api = new MondayApi({
      fetch: () => Promise.reject(new TypeError('Failed to fetch')),
    });

    await expect(
      collect(api.chat(PROJECT, { message: 'x' }, new AbortController().signal)),
    ).rejects.toThrow('Failed to fetch');
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

describe('MondayApi personal tokens', () => {
  function apiWith(respond: (url: string, init: RequestInit) => Response) {
    const calls: { url: string; init: RequestInit }[] = [];
    const api = new MondayApi({
      devUser: 'dev',
      fetch: async (url, init) => {
        calls.push({ url: String(url), init: init ?? {} });
        return respond(String(url), init ?? {});
      },
    });
    return { api, calls };
  }

  it('lists, creates and revokes under /me/tokens', async () => {
    const token = { id: 't1', name: 'laptop', prefix: 'mpat_abc' };
    const { api, calls } = apiWith((_, init) =>
      init.method === 'POST'
        ? json({ token, plain: 'mpat_abc_secret' }, 201)
        : init.method === 'DELETE'
          ? new Response(null, { status: 204 })
          : json([token]),
    );

    expect(await api.tokens()).toMatchObject([token]);
    const input = { name: 'laptop', scope: 'read' as const, projects: null, lifetimeDays: 90 };
    expect(await api.createToken(input)).toMatchObject({ token, plain: 'mpat_abc_secret' });
    await api.revokeToken('t1');

    expect(calls.map((c) => `${c.init.method} ${c.url}`)).toEqual([
      'GET /me/tokens/',
      'POST /me/tokens/',
      'DELETE /me/tokens/t1',
    ]);
    expect(JSON.parse(String(calls[1].init.body))).toEqual(input);
    expect(new Headers(calls[2].init.headers).get('X-Monday-Dev-User')).toBe('dev');
  });

  it('turns a refused create into an ApiError with its code', async () => {
    const { api } = apiWith(() =>
      json({ title: 'A token can last one year at most.', code: 'token.lifetime_max' }, 400),
    );

    await expect(
      api.createToken({ name: 'x', scope: 'read', projects: null, lifetimeDays: 400 }),
    ).rejects.toMatchObject({ status: 400, code: 'token.lifetime_max' });
  });
});

describe('MondayApi model settings', () => {
  it('reads, saves, tests and clears under /projects/{id}/model', async () => {
    const calls: { url: string; init: RequestInit }[] = [];
    const view = { enabled: true, configured: true, keyLast4: 'ABCD' };
    const api = new MondayApi({
      fetch: async (url, init = {}) => {
        calls.push({ url: String(url), init });
        if (init.method === 'DELETE') return new Response(null, { status: 204 });
        if (String(url).endsWith('/test')) return json({ ok: true, code: null, message: null });
        if (String(url).endsWith('/usage')) return json({ month: '2026-10', inputTokens: 1 });
        return json(view);
      },
    });
    const input = {
      provider: 'Gemini' as const,
      model: 'gemini-2.5-pro',
      baseUrl: null,
      apiKey: 'gm-key-12345678',
      monthlyTokenCap: null,
    };

    expect(await api.modelSettings(PROJECT)).toMatchObject(view);
    expect(await api.saveModelSettings(PROJECT, input)).toMatchObject(view);
    expect((await api.testModel(PROJECT)).ok).toBe(true);
    expect((await api.modelUsage(PROJECT)).month).toBe('2026-10');
    await api.clearModelSettings(PROJECT);

    expect(calls.map((c) => `${c.init.method} ${c.url}`)).toEqual([
      `GET /projects/${PROJECT}/model/`,
      `PUT /projects/${PROJECT}/model/`,
      `POST /projects/${PROJECT}/model/test`,
      `GET /projects/${PROJECT}/model/usage`,
      `DELETE /projects/${PROJECT}/model/`,
    ]);
    expect(JSON.parse(String(calls[1].init.body))).toEqual(input);
  });
});

describe('MondayApi fields the server leaves out', () => {
  // The API writes no null fields (WhenWritingNull); the client hands the viewer nulls instead.
  const apiAnswering = (body: unknown) => new MondayApi({ fetch: async () => json(body) });

  it('fills an unconfigured model with nulls', async () => {
    const view = await apiAnswering({ enabled: true, configured: false }).modelSettings(PROJECT);
    expect(view).toEqual({
      enabled: true,
      configured: false,
      provider: null,
      model: null,
      baseUrl: null,
      keyLast4: null,
      monthlyTokenCap: null,
      updatedBy: null,
      updatedAt: null,
    });
  });

  it('fills usage without a cap, and a passing test, with nulls', async () => {
    const usage = { month: '2026-10', inputTokens: 1, outputTokens: 2, capReached: false };
    expect((await apiAnswering(usage).modelUsage(PROJECT)).monthlyTokenCap).toBeNull();
    expect(await apiAnswering({ ok: true }).testModel(PROJECT)).toEqual({
      ok: true,
      code: null,
      message: null,
    });
  });

  it('fills a token without limits, revocation or use with nulls', async () => {
    const token = {
      id: 't1',
      name: 'laptop',
      prefix: 'mpat_abc',
      scope: 'read',
      createdAt: '2026-10-01T00:00:00Z',
      expiresAt: '2026-12-30T00:00:00Z',
      active: true,
    };
    const [listed] = await apiAnswering([token]).tokens();
    expect(listed).toMatchObject({ projects: null, revokedAt: null, lastUsedAt: null });
    const issued = await apiAnswering({ token, plain: 'mpat_abc_secret' }).createToken({
      name: 'laptop',
      scope: 'read',
      projects: null,
      lifetimeDays: null,
    });
    expect(issued.token).toMatchObject({ projects: null, revokedAt: null, lastUsedAt: null });
  });
});
