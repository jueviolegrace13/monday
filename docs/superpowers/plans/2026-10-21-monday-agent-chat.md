# Monday Agent Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Monday becomes an agent people chat with inside the graph view, on any model the project brings, with the same tools reachable over MCP.

**Architecture:** A provider-neutral tool layer in `Monday.Application/Agent` wraps the existing services and is filtered by role. `AgentService` runs a tool loop over an `IChatModel` port whose adapters live in `Monday.Infrastructure/Models`. `Monday.Api` streams chat over SSE and serves `/mcp` from the same registry. A new Angular viewer hosts the graph and the chat panel.

**Tech Stack:** .NET 10, minimal APIs, Npgsql (existing), `System.Security.Cryptography` AES-GCM, `ModelContextProtocol.AspNetCore` (approved 2026-10-21), Angular (approved stack), Playwright (existing MCP).

**Spec:** `docs/superpowers/specs/2026-10-21-monday-agent-chat-design.md`

## Global Constraints

- Production is read-only: no tool writes code, data, config, members, keys or environments. The only write is `draft_decision`, and nothing the agent does can approve one.
- Privacy: tools return only what the existing API returns. No bodies, query values, cookies, credentials, IPs or SQL parameters. User hashes only with `ReplayUserTraffic`.
- Every domain rule has a test in `tests/Monday.Domain.Tests` (the custom runner: `public static void` methods on `*Tests` classes; `Assert.True/Equal/Throws<T>(action, code)`).
- Warnings are errors; analyzers on `latest-recommended`. Async calls use `.ConfigureAwait(false)` like the existing services.
- Dependencies point inward: Domain ← Application ← Infrastructure ← Api.
- No vendor model SDKs: adapters use `HttpClient`.
- Master key from `MONDAY_SECRETS_KEY` only (env), never files or flags.
- Agent tool-step cap: **8** per message.
- Personal tokens: prefix `mpat_`, default expiry **90 days**, max **365 days**, scopes `read` and `read_propose`.
- Model base URL: HTTPS only. Refuse loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10) and metadata (169.254.169.254) addresses, on save and on every call.
- UI values only from `design/tokens.css`; read `design/TASTE.md` before UI work; Playwright on local URLs only.
- After each build session, re-index and record activity events (repo CLAUDE.md).

## Review Focus

1. **A model asks for a tool the person's role hides** (hallucinated or prompt-injected name). Expect a tool error fed back to the model, not an exception and not the tool running. Test in Task 3.
2. **The viewer disconnects mid-answer** (tab closed, Stop). Expect the loop cancelled, no further tool calls, usage up to that point recorded. Test in Task 7.
3. **A provider streams malformed or partial tool-call JSON** (OpenAI sends arguments in fragments). Expect fragments joined, and invalid JSON turned into a tool error rather than a crash. Test in Task 6.
4. **The base URL's hostname resolves to a private address after saving** (DNS rebinding). Expect the call refused at request time. Test in Task 5.
5. **A revoked or expired personal token is still in a client's config.** Expect 401 on `/mcp` with no tool list. Test in Task 11.

---

## Phase 1: Tool layer and roles

### Task 1: Agent drafts in the decisions flow

**Files:**
- Modify: `src/Monday.Domain/Decisions/Decision.cs` (add `DraftedBy`, `Draft` factory)
- Modify: `src/Monday.Application/Decisions/DecisionService.cs` (add `DraftAsync`)
- Modify: `src/Monday.Infrastructure/Postgres/Schema.cs` (new migration), `PostgresStores.cs`, `InMemory/InMemoryStores.cs` if they map fields
- Test: `tests/Monday.Domain.Tests/DecisionDraftTests.cs`

**Interfaces:**
- Produces: `Decision.Draft(ProjectId, DecisionArea, string title, string context, IReadOnlyList<DecisionOption>, string recommendedKey, UserId draftedBy, DateTimeOffset now) : Decision`; `Decision.DraftedBy : UserId?`; `DecisionService.DraftAsync(ProjectId id, DecisionProposal proposal, UserId by, CancellationToken ct) : Task<Decision>` (requires `Permission.ChatAct`).

- [ ] **Step 1: Write failing tests** `DraftRecordsWhoAsked` (`DraftedBy == user`, `Status == Proposed`), `DrafterWhoIsNotOwnerCannotApproveOwnDraft` (`Assert.Throws<DomainException>(…, "access.owner_only")` or the existing resolve code).
- [ ] **Step 2: Run** `dotnet run --project tests/Monday.Domain.Tests`. Expected: build fails, `Draft` not defined.
- [ ] **Step 3: Implement** `Draft` reusing `Propose`'s guards; add migration `ALTER TABLE decisions ADD COLUMN drafted_by_user text NULL;` as a new entry at the end of `Migrations`.
- [ ] **Step 4: Run tests.** Expected: all pass.
- [ ] **Step 5: Commit** `Decisions: members can draft through the agent`.

### Task 2: Tool contracts and registry

**Files:**
- Create: `src/Monday.Application/Agent/AgentTool.cs` (`IAgentTool`, `ToolKind`, `ToolChannel`, `ToolContext`, `ToolResult`, `ScreenAction`)
- Create: `src/Monday.Application/Agent/ToolRegistry.cs`
- Test: `tests/Monday.Domain.Tests/ToolRegistryTests.cs`

**Interfaces:**
- Produces (exact, from spec section 1):
  - `enum ToolKind { Read, ScreenAction, Proposal }`, `enum ToolChannel { Chat, Mcp }`
  - `record ToolContext(ProjectId Project, UserId User, Role Role, ToolChannel Channel)`
  - `record ScreenAction(string Type, JsonElement Args)`, where Type is one of `focus_node`, `highlight_path`, `set_lens`, `start_replay`
  - `record ToolResult(JsonElement Data, IReadOnlyList<string> NodeIds, ScreenAction? Action = null)`
  - `interface IAgentTool { string Name; string Description; JsonElement InputSchema; ToolKind Kind; IReadOnlySet<Permission> Requires; Task<ToolResult> RunAsync(ToolContext, JsonElement input, CancellationToken) }`
  - `sealed class ToolRegistry(IEnumerable<IAgentTool> tools)`: `IReadOnlyList<IAgentTool> For(Role role, ToolChannel channel)`; `IAgentTool? Find(string name, Role role, ToolChannel channel)`
  - `record ToolSpec(string Name, string Description, JsonElement InputSchema)` and `ToolSpec IAgentTool.ToSpec()` extension, used by the model port

- [ ] **Step 1: Failing tests** with three fake tools (Read/ChatAsk, ScreenAction/ChatAct, Proposal/ChatAct): `MonitorSeesOnlyAskTools` (count 1), `DebuggerSeesActions` (count 3), `McpDropsScreenActions` (Debugger on Mcp → 2), `FindRefusesHiddenTool` (Monitor `Find("focus")` → null).
- [ ] **Step 2: Run.** Expected: FAIL, types missing.
- [ ] **Step 3: Implement.** Filter by `Requires.All(p => RolePermissions.Allows(role, p))`; duplicate names throw at construction.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Agent: tool contracts and role-filtered registry`.

### Task 3: v1 tools

**Files:**
- Create: `src/Monday.Application/Agent/Tools/GraphTools.cs` (`graph_overview`, `find_nodes`, `node_detail`, `security_findings`)
- Create: `src/Monday.Application/Agent/Tools/TrafficTools.cs` (`traffic_stats`, `recent_requests`)
- Create: `src/Monday.Application/Agent/Tools/ActivityTools.cs` (`recent_activity`)
- Create: `src/Monday.Application/Agent/Tools/DecisionTools.cs` (`list_decisions`, `draft_decision`)
- Create: `src/Monday.Application/Agent/Tools/ScreenTools.cs` (`focus_node`, `highlight_path`, `set_lens`, `start_replay`)
- Create: `src/Monday.Application/Agent/Tools/ProjectTools.cs` (`list_projects`, Mcp only: `Requires` empty, `Kind = Read`, and registry includes it only on `Mcp`)
- Create: `src/Monday.Application/Agent/AgentToolsRegistration.cs` (`IServiceCollection AddAgentTools(this IServiceCollection)`)
- Test: `tests/Monday.Domain.Tests/AgentToolTests.cs` (in-memory stores from `Monday.Infrastructure`; add that project reference to the test csproj)

**Interfaces:**
- Consumes: Task 1 `DraftAsync`, Task 2 contracts; existing `GraphService.GetAsync/GetNodeAsync`, `TrafficService.StatsAsync/ReplayAsync`, `ActivityService.RangeAsync`, `DecisionService.ListAsync`, `ProjectService.ListAsync`.
- Produces: tool names and `Requires` exactly as the spec table. `start_replay` input `{ since, until, userId? }`; with `userId` it also requires `ReplayUserTraffic` (checked in `RunAsync` through `TrafficService.ReplayAsync`, which audits).
- Every tool takes `project` in its input schema when `Channel == Mcp`; in Chat the project comes from `ToolContext`.
- Result sizes are capped: lists 50 items, code 4,000 chars, so one tool call can't flood the model context.

- [ ] **Step 1: Failing tests:** `MonitorNodeDetailHasNoCode` (`Code == null` in Data), `RecentRequestsNeverCarryUserHashWithoutReplayRights`, `StartReplayForUserNeedsReplayUserTraffic` (Debugger ok, Monitor refused), `DraftDecisionLandsAsProposed`, `ToolResultsCarryNodeIds` (`find_nodes` returns its matches' ids), `ListsCappedAtFifty`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** each tool as a small sealed class; JSON schemas as `JsonDocument.Parse("""…""").RootElement` constants.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Agent: v1 tools over the existing services`.

## Phase 2: Agent loop, adapters, settings and keys

### Task 4: Model port and fake model

**Files:**
- Create: `src/Monday.Application/Abstractions/ChatModel.cs`
- Create: `tests/Monday.Domain.Tests/Fakes/ScriptedChatModel.cs`

**Interfaces:**
- Produces:
  - `interface IChatModel { IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct); }`
  - `record ModelRequest(string Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec> Tools)`
  - `record ChatMessage(ChatRole Role, string? Text, IReadOnlyList<ToolCallPart>? ToolCalls = null, ToolResultPart? ToolResult = null)`; `enum ChatRole { User, Assistant, Tool }`
  - `record ToolCallPart(string Id, string Name, JsonElement Args)`; `record ToolResultPart(string CallId, JsonElement Content, bool IsError)`
  - `abstract record ModelEvent`; `TextDelta(string Text)`, `ToolCall(string Id, string Name, JsonElement Args)`, `Finished(StopReason Reason, Usage Usage)`; `enum StopReason { EndTurn, ToolUse, MaxTokens, Error }`; `record Usage(int InputTokens, int OutputTokens)`
  - `interface IChatModelFactory { IChatModel For(ModelSettings settings, string apiKey); }`
  - `ScriptedChatModel(params IReadOnlyList<ModelEvent>[] turns)`: each `StreamAsync` call yields the next turn and records the request it got.

- [ ] **Step 1:** Write the types; no behaviour to test.
- [ ] **Step 2: Build.** `dotnet build`. Expected: 0 warnings.
- [ ] **Step 3: Commit** `Agent: provider-neutral model port`.

### Task 5: Model settings, key encryption and base URL rules

**Files:**
- Create: `src/Monday.Domain/Agent/ModelSettings.cs` (value object + `ModelProvider` enum)
- Modify: `src/Monday.Domain/Projects/Project.cs` (`ModelSettings? Model`, `SetModel(ModelSettings, UserId by, DateTimeOffset now)`, `ClearModel(UserId by)`, both `ManageMembersAndKeys`, audited via domain event)
- Create: `src/Monday.Application/Abstractions/SecretBox.cs` (`interface ISecretBox { string Seal(string plain); string Open(string sealed); bool Enabled { get; } }`)
- Create: `src/Monday.Infrastructure/Secrets/AesGcmSecretBox.cs`
- Create: `src/Monday.Infrastructure/Models/SafeEndpoint.cs` (`static Task EnsurePublicAsync(Uri uri, CancellationToken ct)` + a `SocketsHttpHandler.ConnectCallback` that re-checks the resolved IP)
- Create: `src/Monday.Application/Agent/ModelSettingsService.cs`
- Modify: Postgres `Schema.cs` (`model_settings`, `model_usage` per spec), stores, in-memory stores
- Test: `tests/Monday.Domain.Tests/ModelSettingsTests.cs`, `SecretBoxTests.cs`, `SafeEndpointTests.cs`

**Interfaces:**
- Produces:
  - `enum ModelProvider { Anthropic, OpenAiCompatible, Gemini }`
  - `record ModelSettings(ModelProvider Provider, string Model, Uri? BaseUrl, string KeyCipher, string KeyLast4, long? MonthlyTokenCap)` with `static ModelSettings Create(…)`. Guards: `model.base_url_https` (non-HTTPS), `model.base_url_only_openai` (BaseUrl on other providers), `model.cap_positive`.
  - `ModelSettingsService`: `SaveAsync(ProjectId, ModelSettingsInput input, UserId by, ct)`, `GetAsync(ProjectId, UserId, ct) : ModelSettingsView` (never the key), `TestAsync(ProjectId, UserId, ct) : ModelTestResult`, `UsageAsync(ProjectId, UserId, ct) : ModelUsageView`.
  - `record ModelSettingsInput(ModelProvider Provider, string Model, string? BaseUrl, string? ApiKey, long? MonthlyTokenCap)` (null `ApiKey` keeps the stored one).
  - `interface IModelUsageStore { Task AddAsync(ProjectId, string month, Usage, ct); Task<Usage> MonthAsync(ProjectId, string month, ct); }` (month = `yyyy-MM`, UTC).
  - Sealed format `v1:<nonce b64>:<cipher b64>:<tag b64>`; master key is 32 bytes base64 in `MONDAY_SECRETS_KEY`. Missing key → `Enabled == false`, `Seal/Open` throw `InvalidOperationException("Model keys are disabled: MONDAY_SECRETS_KEY is not set.")`.

- [ ] **Step 1: Failing tests:** `OnlyOwnersAndAdminsSetModel` (Debugger → `access.*` code), `HttpBaseUrlRefused`, `BaseUrlOnlyForOpenAiCompatible`, `SealOpenRoundTrip`, `TamperedCipherFailsToOpen` (`CryptographicException`), `WrongMasterKeyFailsToOpen`, `LoopbackPrivateLinkLocalAndMetadataRefused` (`127.0.0.1`, `10.1.2.3`, `192.168.0.5`, `169.254.169.254`, `::1`, `fd00::1`), `PublicAddressAllowed` (`1.1.1.1`), `ConnectCallbackRefusesRebindToPrivate` (resolver stub returns `10.0.0.1` for a public name → throws). Covers Review Focus 4.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** `AesGcm` with a 12-byte nonce and 16-byte tag; `KeyLast4` = last 4 chars of the plain key.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Agent: per-project model settings with sealed keys`.

### Task 6: Anthropic and OpenAI-compatible adapters

**Files:**
- Create: `src/Monday.Infrastructure/Models/AnthropicChatModel.cs`
- Create: `src/Monday.Infrastructure/Models/OpenAiCompatibleChatModel.cs`
- Create: `src/Monday.Infrastructure/Models/SseReader.cs` (shared `IAsyncEnumerable<(string Event, string Data)> ReadAsync(Stream, ct)`)
- Create: `src/Monday.Infrastructure/Models/ChatModelFactory.cs` (`IChatModelFactory`; named `HttpClient` "models" with `SafeEndpoint` handler, 60 s timeout)
- Create: `tests/Monday.Domain.Tests/Fixtures/anthropic-tool-stream.txt`, `openai-tool-stream.txt`, `openai-fragmented-args.txt`, `openai-bad-args.txt`
- Test: `tests/Monday.Domain.Tests/ChatModelAdapterTests.cs` (`HttpMessageHandler` stub serving fixtures)

**Interfaces:**
- Consumes: Task 4 port, Task 5 `ModelSettings`, `SafeEndpoint`.
- Anthropic: `POST https://api.anthropic.com/v1/messages`, headers `x-api-key`, `anthropic-version: 2023-06-01`, body `{ model, system, messages, tools:[{name,description,input_schema}], max_tokens: 4096, stream: true }`. Map `content_block_delta` text → `TextDelta`; `input_json_delta` accumulated per block → `ToolCall` at `content_block_stop`; `message_delta.usage` + `stop_reason` → `Finished`.
- OpenAI-compatible: `POST {BaseUrl ?? https://api.openai.com/v1}/chat/completions`, `Authorization: Bearer`, body `{ model, messages, tools:[{type:"function",function:{name,description,parameters}}], stream: true, stream_options:{include_usage:true} }`. Join `delta.tool_calls[i].function.arguments` fragments by index. Emit `ToolCall` at `finish_reason`. Missing usage → `Usage(0,0)`.
- Unparseable tool args → `ToolCall` with `Args = {"_invalid": "<raw>"}`; the loop turns that into a tool error (Review Focus 3).
- HTTP 401/403 → `ModelException("model.auth", "The model provider refused the key.")`; 429 → `"model.rate_limited"`; 5xx/timeout → `"model.unavailable"`. `ModelException(string Code, string Message)` lives in `Monday.Application/Abstractions/ChatModel.cs`.

- [ ] **Step 1: Failing tests:** `AnthropicStreamsTextThenToolCall`, `OpenAiJoinsFragmentedArguments`, `OpenAiBadArgumentsMarkedInvalid`, `UnauthorizedMapsToModelAuth`, `RateLimitMapsToRateLimited`, `RequestCarriesToolsInProviderShape` (assert the serialized body's tool field names).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Agent: Anthropic and OpenAI-compatible adapters`.

### Task 7: Agent loop, history and chat endpoint

**Files:**
- Create: `src/Monday.Domain/Agent/Conversation.cs` (aggregate: owner, project, title from the first message, max 80 chars; `Append(ChatMessage)`; only the owner can read)
- Create: `src/Monday.Application/Agent/AgentService.cs`
- Create: `src/Monday.Application/Agent/AgentInstructions.cs` (`static string Build(Project, Role, string? selectedNodeId)`)
- Create: `src/Monday.Application/Abstractions/ConversationStore.cs` (`IConversationStore`: `GetAsync`, `ListAsync(ProjectId, UserId)`, `SaveAsync`)
- Create: `src/Monday.Api/Endpoints/ChatEndpoints.cs`
- Modify: `src/Monday.Api/Program.cs` (`app.MapChat();`), `src/Monday.Infrastructure/DependencyInjection.cs` (register stores, `ISecretBox`, factory, tools)
- Modify: Postgres `Schema.cs` (`conversations`, `conversation_messages`), stores, in-memory stores
- Test: `tests/Monday.Domain.Tests/AgentLoopTests.cs`, `ConversationTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6.
- Produces:
  - `abstract record AgentEvent`: `AgentText(string Text)`, `AgentTool(string Name, IReadOnlyList<string> NodeIds, bool IsError)`, `AgentAction(ScreenAction Action)`, `AgentDone(Guid ConversationId, Usage Usage)`, `AgentError(string Code, string Message)`
  - `AgentService.ChatAsync(ProjectId id, UserId user, ChatInput input, CancellationToken ct) : IAsyncEnumerable<AgentEvent>`
  - `record ChatInput(Guid? ConversationId, string Message, string? SelectedNodeId)`; message max 4,000 chars.
  - `const int MaxToolSteps = 8;`
  - Endpoint `POST /projects/{id:guid}/chat` (auth required, `ChatAsk`), `text/event-stream`, one SSE event per `AgentEvent` (`event: text|tool|action|done|error`, `data:` JSON). `GET /projects/{id:guid}/conversations` and `GET /projects/{id:guid}/conversations/{cid:guid}` for history.
- Refusals before calling the model: no settings → `AgentError("model.not_configured", …)`; secret box disabled → `"model.keys_disabled"`; month usage ≥ cap → `"model.cap_reached"`.
- A tool name not in `ToolRegistry.Find(...)` or `_invalid` args → `ToolResultPart(IsError: true)` back to the model plus `AgentTool(IsError: true)`; never throws (Review Focus 1).

- [ ] **Step 1: Failing tests** (all with `ScriptedChatModel`): `AnswersWithoutTools`, `RunsToolThenAnswers` (second request contains the tool result), `StopsAfterEightToolSteps` (9th turn never requested; final `AgentError("agent.step_cap", …)`), `HiddenToolBecomesToolError` (Monitor, model calls `focus_node`), `CancellationStopsLoopAndRecordsUsage` (cancel after the first tool; no second model call; usage store has turn 1) (Review Focus 2), `CapReachedRefusesBeforeModelCall`, `ConversationVisibleOnlyToOwner`, `ScreenActionEmittedAsAction`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Usage is added after each model turn, inside `finally` for the cancelled case.
- [ ] **Step 4: Run.** Expected: PASS. Then run the API (`dotnet run --project src/Monday.Api`) and `curl -N -H "X-Monday-Dev-User: rendee" -X POST …/chat` with a configured OpenAI-compatible model (or Ollama if reachable) to see events stream.
- [ ] **Step 5: Commit** `Agent: tool loop, history and streaming chat endpoint`.

### Task 8: Model settings endpoints

**Files:**
- Create: `src/Monday.Api/Endpoints/ModelEndpoints.cs` (`GET/PUT/DELETE /projects/{id}/model`, `POST /projects/{id}/model/test`, `GET /projects/{id}/model/usage`)
- Modify: `Program.cs` (`app.MapModel();`), `render.yaml` (declare `MONDAY_SECRETS_KEY` with `sync: false`)
- Test: `tests/Monday.Domain.Tests/ModelSettingsServiceTests.cs`

**Interfaces:**
- Consumes: Task 5 service.
- `TestAsync` sends one request with a dummy tool `ping` and the prompt "Call the ping tool." It passes only if a `ToolCall` named `ping` comes back.

- [ ] **Step 1: Failing tests:** `GetNeverReturnsKey` (view has `KeyLast4`, no key field), `SaveWithoutKeyKeepsStoredKey`, `TestFailsWhenModelNeverCallsTool` (scripted text-only reply).
- [ ] **Step 2: Run.** FAIL. **Step 3: Implement.** **Step 4: Run.** PASS.
- [ ] **Step 5: Commit** `Agent: model settings API`.

## Phase 3: Angular viewer with the chat panel

### Task 9: Angular app shell served by the API

**Files:**
- Create: `viewer/app/` (Angular CLI project, standalone components, `strict`)
- Create: `viewer/app/src/styles.css` (imports `../../../design/tokens.css`)
- Create: `viewer/app/src/app/api/monday-api.ts` (typed client for projects, graph, node, activity, traffic live, chat SSE)
- Modify: `src/Monday.Api/Program.cs` (`UseDefaultFiles`, `UseStaticFiles` from `wwwroot`; SPA fallback `MapFallbackToFile("index.html")` after API routes)
- Modify: `Dockerfile` (node build stage → `wwwroot`), `.github/workflows/ci.yml` (`npm ci && npm run build && npm test -- --watch=false`)
- Test: `viewer/app/src/app/api/monday-api.spec.ts`

**Interfaces:**
- Produces: `MondayApi.chat(projectId, input, signal): AsyncIterable<AgentEvent>` (parses SSE via `fetch` + `ReadableStream`, since EventSource can't POST), TypeScript `AgentEvent` union mirroring Task 7.
- Dev: `ng serve` proxies `/projects`, `/mcp` to the API and adds `X-Monday-Dev-User` from `environment.devUser`.
- Before writing UI, read `design/TASTE.md`, the images in `design/references/`, and `design/tokens.css`.

- [ ] **Step 1: Failing test** `parsesSseIntoAgentEvents` (feed a stream with text, tool, done).
- [ ] **Step 2: Run** `npm test -- --watch=false`. FAIL.
- [ ] **Step 3: Implement** the client and an empty shell with the project picker (`GET /projects`).
- [ ] **Step 4: Run.** PASS. `npm run build` places output in `src/Monday.Api/wwwroot`.
- [ ] **Step 5: Commit** `Viewer: Angular shell served by the API`.

### Task 10: Graph canvas, chat panel and node pulses

**Files:**
- Create: `viewer/app/src/app/graph/graph-canvas.component.ts` (wraps the drawing code extracted from `viewer/artifact/index.html` into `viewer/app/src/app/graph/graph-engine.ts`, unchanged except data input and an event API)
- Create: `viewer/app/src/app/chat/chat-panel.component.ts`, `chat-store.ts`
- Create: `viewer/app/src/app/node/node-panel.component.ts`
- Create: `viewer/app/e2e/chat.spec.ts` (Playwright against `ng serve` with a stubbed `/chat`)
- Test: `chat-store.spec.ts`

**Interfaces:**
- `GraphEngine`: `load(graph)`, `focus(nodeId)`, `highlightPath(nodeIds)`, `setLens(name)`, `pulse(nodeIds)`, `back()`, `onSelect(cb)`. These are the same operations the artifact viewer already has; only their wiring is new.
- `ChatStore` (signals): `messages`, `streaming`, `send(text)`, `stop()`. `AgentTool` → `engine.pulse(nodeIds)` plus a step line `name · n nodes`. `AgentAction` → the matching engine call. `stop()` aborts the fetch.
- Layout per spec section 3: chat floats docked left, node detail right, both collapsible, `/` opens chat (not while typing in an input), Esc/Back undo screen actions. Monitors see no action affordances; a missing model shows the "not configured" state with a link for Owners and Admins.

- [ ] **Step 1: Failing tests:** `toolEventPulsesNodes`, `actionEventCallsEngine`, `stopAbortsStream`, `selectedNodeSentWithMessage`; e2e `slashOpensChat`, `scriptedAnswerPulsesAndStops`.
- [ ] **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** unit and e2e tests. PASS. Then `/design-loop` on the local URL and fix the worst category once.
- [ ] **Step 5: Commit** `Viewer: graph canvas and chat panel`.

## Phase 4: MCP and personal tokens

### Task 11: Personal access tokens

**Files:**
- Create: `src/Monday.Domain/Access/PersonalToken.cs`
- Create: `src/Monday.Application/Access/PersonalTokenService.cs`, `Abstractions/PersonalTokenStore.cs`
- Create: `src/Monday.Api/Security/PersonalTokenAuthentication.cs` (scheme `MondayToken`, bearer `mpat_…`, used only on `/mcp`)
- Create: `src/Monday.Api/Endpoints/TokenEndpoints.cs` (`GET/POST /me/tokens`, `DELETE /me/tokens/{id}`)
- Modify: Postgres `Schema.cs` (`personal_tokens`), stores
- Test: `tests/Monday.Domain.Tests/PersonalTokenTests.cs`

**Interfaces:**
- Produces: `PersonalToken.Issue(UserId, string name, TokenScope scope, IReadOnlyList<ProjectId>? projects, TimeSpan? lifetime, DateTimeOffset now) : (PersonalToken, string Plain)`. Format `mpat_<prefix>_<secret>`, same hashing as `ApiKey`. `enum TokenScope { Read, ReadPropose }`. `bool Allows(ProjectId, ToolKind)` (Proposal only with `ReadPropose`; project list respected). `Revoke(now)`, `Touch(now)`. Issue and revoke append `AuditEntry`. Rate limit policy `McpEndpoints.RateLimitPolicy` partitioned by token prefix, 60 calls per minute.

- [ ] **Step 1: Failing tests:** `DefaultExpiryIsNinetyDays`, `LifetimeOverOneYearRefused` (`token.lifetime_max`), `ReadScopeCannotPropose`, `ProjectLimitRespected`, `RevokedTokenInactive`, `ExpiredTokenInactive`, `PlainTokenShownOnceAndOnlyHashStored`.
- [ ] **Step 2: Run.** FAIL. **Step 3: Implement.** **Step 4: Run.** PASS.
- [ ] **Step 5: Commit** `Access: personal access tokens`.

### Task 12: MCP endpoint

**Files:**
- Modify: `src/Monday.Api/Monday.Api.csproj` (add `ModelContextProtocol.AspNetCore`, latest stable at build time; record the version in the commit message)
- Create: `src/Monday.Api/Endpoints/McpEndpoints.cs`
- Test: `tests/Monday.Domain.Tests/McpTests.cs` (in-process `WebApplicationFactory`-style host on `Program`; MCP client from the same SDK package)

**Interfaces:**
- Consumes: Task 2 registry with `ToolChannel.Mcp`, Task 11 token auth.
- `/mcp` (Streamable HTTP). `tools/list` = union over the token holder's projects of `ToolRegistry.For(role, Mcp)`, filtered by `PersonalToken.Allows`. Each tool's schema gains a required `project` string (id). `tools/call` resolves the role on that project, re-checks `Allows`, and runs the tool with `ToolChannel.Mcp`. Results are returned as JSON text content.

- [ ] **Step 1: Failing tests:** `ListsOnlyRoleToolsWithoutScreenActions`, `ReadTokenHidesDraftDecision`, `CallOnUnlistedProjectRefused`, `RevokedTokenGets401` and `ExpiredTokenGets401` (Review Focus 5).
- [ ] **Step 2: Run.** FAIL. **Step 3: Implement.** **Step 4: Run.** PASS. Then connect Claude Code locally: `claude mcp add --transport http monday http://localhost:5xxx/mcp --header "Authorization: Bearer mpat_…"` and call `graph_overview`.
- [ ] **Step 5: Commit** `MCP: Monday's tools at /mcp with personal tokens`.

### Task 13: Token screen in the viewer

**Files:** `viewer/app/src/app/settings/tokens.component.ts` (+ spec)

- [ ] **Step 1: Failing test** `newTokenShownOnceThenMasked`. **Step 2: Run.** FAIL.
- [ ] **Step 3: Implement** list (name, scope, projects, expiry, last used), create (shows the plain token once, with copy), revoke.
- [ ] **Step 4: Run.** PASS. **Step 5: Commit** `Viewer: personal tokens screen`.

## Phase 5: Gemini, model settings screen and usage

### Task 14: Gemini adapter

**Files:** `src/Monday.Infrastructure/Models/GeminiChatModel.cs`, fixture `gemini-tool-stream.txt`, tests in `ChatModelAdapterTests.cs`

- Endpoint `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse`, header `x-goog-api-key`. `tools:[{functionDeclarations:[…]}]`. `functionCall` parts → `ToolCall` (Gemini has no call id: use `"{name}-{index}"`). `usageMetadata` → `Usage`.

- [ ] **Step 1: Failing tests** `GeminiStreamsTextThenFunctionCall`, `GeminiUnauthorizedMapsToModelAuth`. **Step 2–4:** fail, implement, pass.
- [ ] **Step 5: Commit** `Agent: Gemini adapter`.

### Task 15: Model settings screen with usage

**Files:** `viewer/app/src/app/settings/model.component.ts` (+ spec)

- Provider select; base URL field only for OpenAI-compatible; key field write-only (placeholder `••••<last4>`); Test connection; this month's usage against the cap. Owners and Admins only.

- [ ] **Step 1: Failing tests** `baseUrlOnlyForOpenAiCompatible`, `keyNeverPrefilled`, `testResultShown`. **Step 2–4:** fail, implement, pass.
- [ ] **Step 5: Commit** `Viewer: model settings and usage`.

### Task 16: Wrap-up

- [ ] Run everything: `dotnet build`, `dotnet run --project tests/Monday.Domain.Tests`, `npm test -- --watch=false`, e2e.
- [ ] Re-index: `dotnet run --project src/Monday.Indexer -- --root .` and record activity events for this session with the node ids touched.
- [ ] Update `README.md` status table (chat moves from planned to done) and the "Add a project" steps (set the project's model).
- [ ] Log in Sunday: changelog entry for what shipped.

## Before building: what Rendee should know

- **Sign-in:** the viewer works locally with the dev header. For other people to sign in on the deployed instance, the separate GitHub sign-in item (needs a GitHub OAuth app from Rendee) must land first. It isn't part of this plan.
- **Toolchain:** this build workspace has no .NET SDK installed; Task 1 starts by installing .NET 10 with the official `dotnet-install.sh` script, and NuGet must be reachable for the MCP SDK (Task 12).
