import { SseFrame, readSse } from './sse';

// ---- Shapes the API returns (camelCase JSON; enums as strings) -------------------------------

export type Role = string;
export type EnvironmentKind = string;

export interface EnvironmentSummary {
  id: string;
  name: string;
  kind: EnvironmentKind;
  readOnly: boolean;
}

export interface ProjectSummary {
  id: string;
  name: string;
  role: Role;
  environments: EnvironmentSummary[];
}

export type NodeKind =
  | 'App'
  | 'Module'
  | 'Feature'
  | 'Type'
  | 'Method'
  | 'Endpoint'
  | 'Test'
  | 'Middleware'
  | 'RouteGroup';
export type EdgeKind =
  'Contains' | 'Calls' | 'DependsOn' | 'Implements' | 'Covers' | 'HandledBy' | 'Next';
export type Severity = 'Low' | 'Medium' | 'High' | 'Critical';

export interface CodeNode {
  id: string;
  kind: NodeKind;
  name: string;
  parentId?: string;
  file?: string;
  startLine?: number;
  endLine?: number;
  code?: string;
  codeHash?: string;
  route?: string;
  guards: string[];
}

export interface CodeEdge {
  from: string;
  to: string;
  kind: EdgeKind;
}

export interface SecurityFinding {
  rule: string;
  severity: Severity;
  nodeId: string;
  title: string;
  why: string;
  fix: string;
  file?: string;
  line?: number;
}

export interface GraphView {
  commit: string;
  indexedAt: string;
  nodes: CodeNode[];
  edges: CodeEdge[];
  findings: SecurityFinding[];
  findingCounts: Record<string, number>;
}

export interface NodeDetail {
  node: CodeNode;
  explanation?: string;
  explanationIsCurrent: boolean;
  callers: CodeNode[];
  callees: CodeNode[];
  tests: CodeNode[];
  endpoints: CodeNode[];
}

export interface ActivityEvent {
  projectId: string;
  sessionId: string;
  at: string;
  kind: string;
  summary: string;
  nodeIds: string[];
}

/** A request as the viewer sees it: how it moved, never what it carried. */
export interface TrafficView {
  traceId: string;
  at: string;
  method: string;
  route: string;
  status: number;
  durationMs: number;
  outcome: string;
  guards: string[];
  userHash?: string;
  errorType?: string;
  nodeId: string;
}

export interface ConversationSummary {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface ConversationMessage {
  role: 'User' | 'Assistant';
  text?: string;
  tools: string[];
  at: string;
}

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ConversationMessage[];
}

// ---- Personal access tokens ---------------------------------------------------------------

/** `read`, or `read_propose`: read and also propose a decision (never approve one). */
export type TokenScope = 'read' | 'read_propose';

/** A token as the list shows it: its public prefix, never the secret. */
export interface PersonalTokenView {
  id: string;
  name: string;
  prefix: string;
  scope: TokenScope;
  /** The projects it is limited to; null means every project its owner is a member of. */
  projects: string[] | null;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  lastUsedAt: string | null;
  active: boolean;
}

/** Null projects means all of the person's projects; null lifetime means 90 days. */
export interface PersonalTokenInput {
  name: string;
  scope: TokenScope;
  projects: string[] | null;
  lifetimeDays: number | null;
}

/** A new token. `plain` is in this one response only; the API never returns it again. */
export interface IssuedToken {
  token: PersonalTokenView;
  plain: string;
}

// ---- Chat ----------------------------------------------------------------------------------

/** What the person asks; the server finds the rest (who they are, what they may see). */
export interface ChatInput {
  message: string;
  conversationId?: string;
  selectedNodeId?: string;
}

export interface Usage {
  inputTokens: number;
  outputTokens: number;
}

/**
 * One streamed chat event. Mirrors the server's AgentEvent records, with the SSE event name as the
 * `event` discriminator (the `action` payload has its own `type`, so `type` cannot be the tag).
 */
export type AgentEvent =
  | { event: 'started'; conversationId: string }
  | { event: 'text'; text: string }
  | { event: 'tool'; name: string; nodeIds: string[]; isError: boolean }
  | { event: 'action'; type: string; args: Record<string, unknown> }
  | { event: 'done'; conversationId: string; usage: Usage }
  | { event: 'error'; code: string; message: string };

const AGENT_EVENTS: ReadonlySet<string> = new Set([
  'started',
  'text',
  'tool',
  'action',
  'done',
  'error',
]);

// ---- Client --------------------------------------------------------------------------------

/** A response that was not 2xx. `code` is the API's stable problem code when it sent one. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly code?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** Reads a problem response; falls back to the status when the body is not JSON. */
  static async from(response: Response): Promise<ApiError> {
    let problem: { title?: string; detail?: string; code?: string } = {};
    try {
      problem = (await response.json()) as typeof problem;
    } catch {
      // Not a problem document: the status says enough.
    }
    return new ApiError(
      response.status,
      problem.detail ?? problem.title ?? `Request failed (${response.status}).`,
      problem.code,
    );
  }
}

export interface MondayApiOptions {
  fetch: (input: string, init?: RequestInit) => Promise<Response>;
  /** Development only: signs in through the API's X-Monday-Dev-User header. Empty in production. */
  devUser?: string;
  baseUrl?: string;
}

export class MondayApi {
  private readonly fetchFn: MondayApiOptions['fetch'];
  private readonly devUser: string;
  private readonly baseUrl: string;

  constructor(options: MondayApiOptions) {
    this.fetchFn = options.fetch;
    this.devUser = options.devUser ?? '';
    this.baseUrl = options.baseUrl ?? '';
  }

  projects(signal?: AbortSignal): Promise<ProjectSummary[]> {
    return this.get('/projects/', signal);
  }

  /** The latest code graph, or null when nothing has been indexed yet. */
  graph(projectId: string, signal?: AbortSignal): Promise<GraphView | null> {
    return this.get(`/projects/${projectId}/graph`, signal);
  }

  node(projectId: string, nodeId: string, signal?: AbortSignal): Promise<NodeDetail> {
    return this.get(
      `/projects/${projectId}/graph/node?nodeId=${encodeURIComponent(nodeId)}`,
      signal,
    );
  }

  activity(
    projectId: string,
    from: Date,
    to: Date,
    signal?: AbortSignal,
  ): Promise<ActivityEvent[]> {
    const range = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;
    return this.get(`/projects/${projectId}/activity?${range}`, signal);
  }

  conversations(projectId: string, signal?: AbortSignal): Promise<ConversationSummary[]> {
    return this.get(`/projects/${projectId}/conversations`, signal);
  }

  conversation(
    projectId: string,
    conversationId: string,
    signal?: AbortSignal,
  ): Promise<Conversation> {
    return this.get(`/projects/${projectId}/conversations/${conversationId}`, signal);
  }

  /** The signed-in person's own personal access tokens, newest first. */
  tokens(signal?: AbortSignal): Promise<PersonalTokenView[]> {
    return this.get('/me/tokens/', signal);
  }

  /** Creates a token. The plain token comes back here once and is never sent again. */
  async createToken(input: PersonalTokenInput, signal?: AbortSignal): Promise<IssuedToken> {
    const response = await this.send('/me/tokens/', {
      method: 'POST',
      headers: this.headers({ 'Content-Type': 'application/json', Accept: 'application/json' }),
      body: JSON.stringify(input),
      signal,
    });
    return (await response.json()) as IssuedToken;
  }

  async revokeToken(id: string, signal?: AbortSignal): Promise<void> {
    await this.send(`/me/tokens/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: this.headers({}),
      signal,
    });
  }

  /** Requests as they happen (server-sent events). Ends when the signal aborts or the server closes. */
  async *liveTraffic(projectId: string, signal?: AbortSignal): AsyncGenerator<TrafficView> {
    const response = await this.open(`/projects/${projectId}/traffic/live`, {
      method: 'GET',
      headers: this.headers({ Accept: 'text/event-stream' }),
      signal,
    });
    if (!response) return;
    for await (const frame of readSse(this.bodyOf(response), signal)) {
      const view = parseJson<TrafficView>(frame);
      if (frame.event === 'request' && view) yield view;
    }
  }

  /**
   * Asks the agent a question and yields its events as they stream in. Uses fetch rather than
   * EventSource, which cannot POST. A refusal before the first event (not a member, message too
   * long...) throws an ApiError; aborting the signal (Stop) ends the stream quietly.
   */
  async *chat(
    projectId: string,
    input: ChatInput,
    signal?: AbortSignal,
  ): AsyncGenerator<AgentEvent> {
    const response = await this.open(`/projects/${projectId}/chat`, {
      method: 'POST',
      headers: this.headers({ 'Content-Type': 'application/json', Accept: 'text/event-stream' }),
      body: JSON.stringify(input),
      signal,
    });
    if (!response) return;
    for await (const frame of readSse(this.bodyOf(response), signal)) {
      const data = parseJson<Record<string, unknown>>(frame);
      if (data && typeof data === 'object' && AGENT_EVENTS.has(frame.event)) {
        yield { ...data, event: frame.event } as AgentEvent;
      }
    }
  }

  private async get<T>(path: string, signal?: AbortSignal): Promise<T> {
    const response = await this.send(path, {
      method: 'GET',
      headers: this.headers({ Accept: 'application/json' }),
      signal,
    });
    if (response.status === 204) return null as T;
    return (await response.json()) as T;
  }

  private headers(extra: Record<string, string>): Headers {
    const headers = new Headers(extra);
    if (this.devUser) headers.set('X-Monday-Dev-User', this.devUser);
    return headers;
  }

  private async send(path: string, init: RequestInit): Promise<Response> {
    const response = await this.fetchFn(this.baseUrl + path, init);
    if (!response.ok) throw await ApiError.from(response);
    return response;
  }

  /** Opens a stream; null when the signal aborted first (Stop before the headers arrived). */
  private async open(path: string, init: RequestInit): Promise<Response | null> {
    try {
      return await this.send(path, init);
    } catch (error) {
      if (init.signal?.aborted) return null;
      throw error;
    }
  }

  private bodyOf(response: Response): ReadableStream<Uint8Array> {
    if (!response.body) throw new ApiError(response.status, 'The server sent no stream.');
    return response.body;
  }
}

function parseJson<T>(frame: SseFrame): T | null {
  try {
    return JSON.parse(frame.data) as T;
  } catch {
    return null;
  }
}
