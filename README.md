# Monday

Mission control for apps that Claude builds. One Monday instance watches many projects:

- **Code graph** — every module, type, method and endpoint, with who calls what and which tests cover it.
- **Activity** — what Claude is doing, live, lit up on the graph.
- **Security lens** — the guards on each endpoint and the request pipeline they sit in.
- **Traffic** (coming) — live requests, replay by time window or user, alerts.
- **Decisions** — Claude proposes, the Owner approves.
- **Agent** — ask about the app in the graph (`/`), with each project's own model; the same tools over MCP.

Production is read-only: Monday watches it, never changes it.

## Status

| Phase | What | State |
| --- | --- | --- |
| 0 | Solution, domain, API, CI | Done |
| 1 | Code graph indexer + Monday Graph viewer | In progress |
| 2 | Agent chat in the graph; bring-your-own model (Anthropic, OpenAI-compatible, Gemini); MCP at `/mcp` with personal tokens; Angular viewer (graph, chat, model settings, tokens) | Done on this branch, not released |
| 2 | Postgres store, GitHub sign-in | In progress: Postgres stores for chat, tokens and model usage are written but not yet run against a database; GitHub sign-in needs an OAuth app |
| 3–7 | Request path, live traffic, replay and archive, second project | Planned |

Publishing Monday for other organisations is not decided.

## Add a project (any time)

Monday watches itself for now. Adding another project later takes no change to Monday's code:

1. **Register it.** Create the project in Monday and issue an API key per environment. Mark production
   read-only (Owner only).
2. **Map its code.** Run `monday-index --root <repo> --name "<Project>"` (C# today; more languages to come),
   or add the indexer step to the project's CI with `MONDAY_URL` and `MONDAY_KEY` as secrets.
3. **Show it in the Monday Graph.** Publish the graph file beside the viewer and add a row to the viewer's
   `projects` registry: `{ key, name, file, live, order }`. Its activity goes in `activity-<key>`.
4. **Set the project's model.** An Owner or Admin picks the provider, model and key in the viewer's Model
   settings, and can set a monthly token cap. The server needs `MONDAY_SECRETS_KEY` to seal the key.
5. **Connect an MCP client (optional).** Create a personal token in the viewer's Tokens screen, then:
   `claude mcp add --transport http monday <url>/mcp --header "Authorization: Bearer mpat_…"`
6. **Live traffic (when the collector is deployed).** Install the Monday agent in the app with its key.

See `CLAUDE.md` for the rules and layout.
