# Monday

Mission control for apps that Claude builds. One Monday instance watches many projects:

- **Code graph** — every module, type, method and endpoint, with who calls what and which tests cover it.
- **Activity** — what Claude is doing, live, lit up on the graph.
- **Security lens** — the guards on each endpoint and the request pipeline they sit in.
- **Traffic** (coming) — live requests, replay by time window or user, alerts.
- **Decisions** — Claude proposes, the Owner approves.

Production is read-only: Monday watches it, never changes it.

## Status

| Phase | What | State |
| --- | --- | --- |
| 0 | Solution, domain, API, CI | Done |
| 1 | Code graph indexer + Monday Graph viewer | In progress |
| 2 | Postgres store, GitHub sign-in, agent, Angular viewer | Next |
| 3–7 | Request path, live traffic, replay and archive, chat, second project | Planned |

## Add a project (any time)

Monday watches itself for now. Adding another project later takes no change to Monday's code:

1. **Register it.** Create the project in Monday and issue an API key per environment. Mark production
   read-only (Owner only).
2. **Map its code.** Run `monday-index --root <repo> --name "<Project>"` (C# today; more languages to come),
   or add the indexer step to the project's CI with `MONDAY_URL` and `MONDAY_KEY` as secrets.
3. **Show it in the Monday Graph.** Publish the graph file beside the viewer and add a row to the viewer's
   `projects` registry: `{ key, name, file, live, order }`. Its activity goes in `activity-<key>`.
4. **Live traffic (when the collector is deployed).** Install the Monday agent in the app with its key.

See `CLAUDE.md` for the rules and layout.
