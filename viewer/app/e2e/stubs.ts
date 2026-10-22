import { Page } from '@playwright/test';

// A small project and graph, and a chat stream the test drives event by event. Everything the
// viewer asks the API for is answered here; /chat never reaches a server.

export const PROJECT = '11111111-1111-1111-1111-111111111111';

const node = (id: string, kind: string, name: string, parentId?: string, extra: object = {}) => ({
  id,
  kind,
  name,
  parentId,
  guards: [],
  ...extra,
});

export const GRAPH = {
  commit: 'abc1234',
  indexedAt: '2026-10-21T00:00:00Z',
  nodes: [
    node('app', 'App', 'Monday'),
    node('module:Monday.Api', 'Module', 'Monday.Api', 'app'),
    node('type:Monday.Api.Projects', 'Type', 'Projects', 'module:Monday.Api'),
    node('method:List', 'Method', 'List', 'type:Monday.Api.Projects', {
      file: 'src/Projects.cs',
      startLine: 10,
      endLine: 20,
    }),
    node('method:Get', 'Method', 'Get', 'type:Monday.Api.Projects'),
    node('method:Save', 'Method', 'Save', 'type:Monday.Api.Projects'),
    node('endpoint:GET /projects', 'Endpoint', 'GET /projects', 'module:Monday.Api', {
      route: 'GET /projects',
      guards: ['RequireAuthorization'],
    }),
  ],
  edges: [
    { from: 'endpoint:GET /projects', to: 'method:List', kind: 'HandledBy' },
    { from: 'method:List', to: 'method:Get', kind: 'Calls' },
  ],
  findings: [],
  findingCounts: {},
};

export async function stubApi(page: Page, role = 'Owner'): Promise<void> {
  await page.route('**/projects/**', async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/projects/')
      return route.fulfill({ json: [{ id: PROJECT, name: 'Monday', role, environments: [] }] });
    if (url.pathname.endsWith('/graph')) return route.fulfill({ json: GRAPH });
    if (url.pathname.endsWith('/graph/node')) {
      const id = url.searchParams.get('nodeId')!;
      const n = GRAPH.nodes.find((x) => x.id === id);
      return route.fulfill({
        json: {
          node: n,
          explanationIsCurrent: true,
          callers: [],
          callees: [],
          tests: [],
          endpoints: [],
        },
      });
    }
    return route.fulfill({ status: 404, json: { title: 'Not stubbed' } });
  });

  // The chat stream: page.route answers a request in one piece, so /chat is stubbed at fetch, where
  // the test can send events one at a time and see Stop abort the request.
  await page.addInitScript(() => {
    type Ctl = ReadableStreamDefaultController<Uint8Array>;
    const state: { ctl: Ctl | null; aborted: boolean; bodies: string[] } = {
      ctl: null,
      aborted: false,
      bodies: [],
    };
    const enc = new TextEncoder();
    (window as unknown as Record<string, unknown>)['__chat'] = {
      state,
      send: (event: string, data: unknown) =>
        state.ctl?.enqueue(enc.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`)),
      close: () => state.ctl?.close(),
    };
    const real = window.fetch.bind(window);
    window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      if (!/\/projects\/[^/]+\/chat$/.test(url)) return real(input, init);
      state.bodies.push(String(init?.body ?? ''));
      state.aborted = false;
      const body = new ReadableStream<Uint8Array>({
        start: (ctl) => {
          state.ctl = ctl;
          init?.signal?.addEventListener('abort', () => {
            state.aborted = true;
            try {
              ctl.error(new DOMException('The operation was aborted.', 'AbortError'));
            } catch {
              // already closed
            }
          });
        },
      });
      return Promise.resolve(
        new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }),
      );
    };
  });
}

/** Sends one SSE event on the open chat stream. */
export const chatEvent = (page: Page, event: string, data: unknown) =>
  page.evaluate(
    ([e, d]) =>
      (window as unknown as { __chat: { send: (e: string, d: unknown) => void } }).__chat.send(
        e as string,
        d,
      ),
    [event, data] as const,
  );

export const chatState = (page: Page) =>
  page.evaluate(
    () =>
      (window as unknown as { __chat: { state: { aborted: boolean; bodies: string[] } } }).__chat
        .state,
  );
