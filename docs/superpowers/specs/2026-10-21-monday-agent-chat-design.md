# Monday agent: chat inside the graph

**Date:** 2026-10-21 · **Status:** for Rendee's review · **Decided with:** Rendee, Monday project thread

## Goal

Monday becomes an agent people talk to inside the graph view, so they can ask questions and get
work done while they watch the app's activity live. It must work for any group on their own
projects, and must not be tied to one model provider.

**Success looks like:** a member opens a project in the viewer, presses `/`, asks "why is
`POST /orders` slow today?", and watches the agent read traffic and the graph, light up the nodes
it looks at, and answer, with the graph focused on the slow path. The same tools are reachable from
Claude Code or Cursor over MCP.

## Scope

**In (v1):**

- Ask and explain: graph, code, activity, traffic, security findings, decisions.
- Actions inside Monday: focus a node, highlight a request path, switch lens, start a replay,
  draft a decision for an Owner to approve.
- Bring-your-own-model per project: Anthropic, any OpenAI-compatible endpoint, Gemini (last step).
- Chat panel in a new Angular viewer (tight scope: project picker, graph canvas, chat).
- MCP endpoint with personal access tokens.

**Out (later):**

- Handing coding work to Claude on a dev branch (designed so it can slot in as another tool kind).
- OAuth sign-in for MCP (needed for claude.ai and ChatGPT connectors).
- Publishing Monday as a product for other organisations (Rendee: decide later). The design stays
  product-ready: everything is per project, keys belong to each project, roles gate every tool.
- The rest of the Angular viewer (endpoint console, settings beyond model and tokens, etc.).

## Rules this design keeps

- **Production is read-only.** No tool writes code, data, config, members, keys or environments.
  The only write is a *draft* decision, which an Owner must approve; the agent can never approve.
- **Approval gates.** Drafted decisions go through the existing decisions flow.
- **Privacy.** Tools return only what the API already returns: no bodies, query values, cookies,
  credentials, IPs or SQL parameters; user hashes only for people with `ReplayUserTraffic`.
- **Free tiers.** Each project pays for its own model with its own key; Monday pays nothing.
- **No secrets in the repo.** The master key for stored model keys comes from `MONDAY_SECRETS_KEY`.

## 1. Tools and roles

New folder `src/Monday.Application/Agent/`.

```csharp
public enum ToolKind { Read, ScreenAction, Proposal }

public interface IAgentTool
{
    string Name { get; }                  // snake_case, e.g. "traffic_stats"
    string Description { get; }
    JsonElement InputSchema { get; }      // JSON Schema, provider-neutral
    ToolKind Kind { get; }
    IReadOnlySet<Permission> Requires { get; } // always includes ChatAsk or ChatAct
    Task<ToolResult> RunAsync(ToolContext context, JsonElement input, CancellationToken ct);
}

public sealed record ToolContext(ProjectId Project, UserId User, Role Role, ToolChannel Channel);
public enum ToolChannel { Chat, Mcp }

public sealed record ToolResult(JsonElement Data, IReadOnlyList<string> NodeIds, ScreenAction? Action = null);
```

`ToolRegistry.For(role, channel)` returns only the tools the role allows (via the existing
`RolePermissions` table) and, on `Mcp`, drops `ScreenAction` tools. The model is only ever shown
that list. Each tool calls the existing services (`GraphService`, `TrafficService`,
`ActivityService`, `DecisionService`), which check permissions again in the domain, so a model
that is tricked into calling something still cannot exceed the person's role.

**v1 tools**

| Tool | Kind | Needs |
| --- | --- | --- |
| `list_projects` (MCP only) | Read | membership |
| `graph_overview` | Read | ChatAsk, ViewLiveGraph |
| `find_nodes` | Read | ChatAsk, ViewLiveGraph |
| `node_detail` (code only with ViewCode) | Read | ChatAsk, ViewLiveGraph |
| `traffic_stats` | Read | ChatAsk, ViewTrafficTotals |
| `recent_requests` | Read | ChatAsk, InspectRequests |
| `recent_activity` | Read | ChatAsk, ViewLiveGraph |
| `security_findings` | Read | ChatAsk, ViewCode |
| `list_decisions` | Read | ChatAsk |
| `focus_node`, `highlight_path`, `set_lens` | ScreenAction | ChatAct |
| `start_replay` (user replay also needs ReplayUserTraffic, audited) | ScreenAction | ChatAct, ReplayAppTraffic |
| `draft_decision` | Proposal | ChatAct |

`DecisionService` gains a `DraftAsync(ProjectId, DecisionProposal, UserId by)` path for drafts
raised by a member through the agent (today proposals only come from an `AppReporter`). The
decision records that it was drafted by the agent on behalf of that member.

Every result carries the node ids it touched, so the viewer can light them up.

## 2. Agent loop and model adapters

**Port** (`Monday.Application/Abstractions`):

```csharp
public interface IChatModel
{
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}

public sealed record ModelRequest(string Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec> Tools);
// ModelEvent: TextDelta(string) | ToolCall(id, name, JsonElement args) | Finished(StopReason, Usage)
```

`IChatModelFactory.For(ModelSettings)` builds the adapter for a project's settings.

**Adapters** (`Monday.Infrastructure/Models/`), plain `HttpClient`, no vendor SDKs:

- `AnthropicChatModel`: Messages API with streaming and tool use.
- `OpenAiCompatibleChatModel`: Chat Completions with streaming and `tools`; base URL configurable
  (OpenAI, OpenRouter, Groq, Mistral, a reachable Ollama server).
- `GeminiChatModel`: last build step.

**Loop** (`AgentService.ChatAsync`):

1. Load the project's model settings; refuse if none or if the monthly cap is reached.
2. Build instructions: project name, the person's role, Monday's rules (read-only, privacy, cite
   node ids), the selected node if any.
3. Send history + new message + `ToolRegistry.For(role, Chat)`.
4. Stream text out. On tool calls, run each through the registry as that person, stream a
   `tool` event (name, node ids) and any `action`, append results, and go back to 3.
5. Stop at 8 tool steps per message; the agent then says it stopped.
6. Record token usage against the project's month.

**Endpoint:** `POST /projects/{id}/chat` (`{ conversationId?, message, selectedNodeId? }`) answers as
`text/event-stream`, like `/traffic/live`. Events: `text`, `tool`, `action`, `done`, `error`.
Closing the stream (Stop button) cancels the loop.

**History:** `Conversation` (project, owner user, title, created) and its messages (role, text,
tool calls and results) are stored per person and visible only to them. No keys are ever stored in
history.

**Failures:** bad key, rate limit, provider outage or a model without tool support come back as one
plain `error` event; no automatic retries. Tool output (route names, commit messages) is treated as
data; the worst a prompt injection can do is mislead an answer or draft a decision that still
needs approval.

## 3. Chat panel in the graph (Angular viewer)

New `viewer/app/` Angular project, served by `Monday.Api` as static files on the same origin, with
GitHub sign-in (already planned) and `X-Monday-Dev-User` on Development.

**Scope:** project picker, graph canvas, chat panel, node detail panel, and the two settings
screens this feature needs (model settings, personal tokens). The graph canvas wraps the existing
drawing code from `viewer/artifact/index.html` in an Angular component rather than rewriting it, so
the approved look and behaviour carry over. The claude.ai Monday Graph keeps working meanwhile.

**Layout** (per `design/TASTE.md`, values only from `design/tokens.css`):

- Chat floats over the graph, docked left; node detail stays right. Both collapse; `/` opens chat.
- The graph stays live underneath while chatting.
- Nodes a tool touches pulse in the accent colour while the answer streams, then settle.
- Each tool step is one compact mono line (`traffic_stats · 3 nodes`); clicking it focuses them.
- Screen actions run immediately; Back/Esc undoes them as today.
- A Stop button ends an answer.
- The selected node is sent as context.
- Monitors get ask-only; Debuggers and up get actions. With no model configured, the panel says so
  and links Owners and Admins to model settings.

## 4. MCP and personal tokens

**Endpoint:** `/mcp` on `Monday.Api`, Streamable HTTP transport, using the official C# MCP SDK
(`ModelContextProtocol.AspNetCore`, approved 2026-10-21). Exposes `ToolRegistry.For(role, Mcp)`:
read tools and `draft_decision`, plus `list_projects`. Every other tool takes `project`. Tool lists
and calls are checked against the caller's role on that project.

**Personal access tokens** (new aggregate `Monday.Domain/Access/PersonalToken`):

- Created by a member in the viewer; shown once; stored as a hash; prefix `mpat_`.
- Scope: `read` or `read_propose`. Optional list of projects it is limited to.
- Expiry: 90 days by default, one year at most. Revocable. Last-used time recorded.
- Creating and revoking are audited. Calls are rate-limited per token.
- Sent as `Authorization: Bearer mpat_…`.

## 5. Model settings and key storage

**`ModelSettings`** on the project (domain value object), changed only by Owners and Admins
(`ManageMembersAndKeys`):

- Provider (`Anthropic`, `OpenAiCompatible`, `Gemini`), model name, base URL (OpenAI-compatible
  only), encrypted key, key last 4, monthly token cap (optional).
- Base URL rules: HTTPS only; hostnames that resolve to loopback, private, link-local or metadata
  addresses are refused (checked on save and on every call).
- **Test connection** sends one tiny request with a dummy tool and fails if tools aren't supported.
- The key is never returned by the API; only its last 4 characters. Changes are audited.
- Usage: input and output tokens counted per project per month and shown in settings; at the cap,
  chat pauses until next month or until the cap is raised.

**Key storage (approved 2026-10-21):** AES-GCM via `System.Security.Cryptography` (no new
dependency) with a master key from `MONDAY_SECRETS_KEY`. Stored as `version:nonce:ciphertext:tag`
so the master key can be rotated. A database leak alone reveals no model keys. Without
`MONDAY_SECRETS_KEY`, the API starts but chat and model settings are disabled with a clear message.

## Data changes (schema migration)

- `model_settings` (project_id, provider, model, base_url, key_cipher, key_last4, monthly_cap, updated_by, updated_at)
- `model_usage` (project_id, month, input_tokens, output_tokens)
- `conversations` (id, project_id, user_id, title, created_at, updated_at)
- `conversation_messages` (id, conversation_id, seq, role, content_json, created_at)
- `personal_tokens` (id, user_id, prefix, hash, scope, project_ids, expires_at, revoked_at, last_used_at, created_at)
- `decisions` gains `drafted_by_user` (nullable)

In-memory implementations come first, behind the same ports, as with the other stores.

## Build order

Each step is usable and tested on its own.

1. Tool layer with role checks (`IAgentTool`, registry, v1 tools, `DraftAsync`).
2. Agent loop, `IChatModel`, Anthropic and OpenAI-compatible adapters, model settings and key
   encryption (API only), chat endpoint and history.
3. Angular viewer: picker, graph canvas, chat panel, model settings screen.
4. MCP endpoint, personal tokens and their screen.
5. Gemini adapter; usage and cap display.

## Testing

- **Domain** (`tests/Monday.Domain.Tests`): personal token rules (expiry cap, revoke, scope,
  project limits); model settings rules (base URL, cap); drafted decisions can't approve themselves.
- **Application:** per role, which tools are visible and which are refused; each tool's permission
  check; loop with a scripted fake model (tool calls, step cap, refusal, cancellation, cap reached).
- **Infrastructure:** adapters against saved sample streams; encryption round-trip, tamper and
  wrong-master-key failures; private-address refusal.
- **API:** chat stream event order; MCP list and call against a test server with a token.
- **Viewer:** Playwright on a local run: `/` opens chat, a scripted answer pulses nodes, Stop works.

## Monday watches itself

After each build session, re-index and record activity events with the node ids touched, as the
repo's CLAUDE.md requires.
