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

## Connect a project

1. In Monday, create the project and issue an API key for its environment.
2. Add the key to the repo's CI secrets as `MONDAY_KEY` and Monday's URL as `MONDAY_URL`.
3. Add the indexer step to CI (see `.github/workflows/ci.yml`).

See `CLAUDE.md` for the rules and layout.
