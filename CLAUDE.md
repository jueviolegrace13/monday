# Monday

Monday is one deployed service that watches any number of apps: their code graph, their security
layers, their traffic, and what Claude does while building them. Rendee decides; Claude builds.
The full plan is the "Monday — Plan" doc; decisions are logged in Sunday under `01-Projects/monday/`.

## Rules that are never bent

1. **Production is read-only.** Monday monitors production and never changes it: no code, data,
   config or infrastructure changes, no test requests. Every write path calls
   `ProductionGuard.EnsureWritable` and the guard has no override. Changes happen locally or in
   development, then reach production through Rendee's normal release.
2. **Approval gates.** Architecture, stack, schema changes, new dependencies, security choices and
   deploys are proposed to Rendee (2–3 options, trade-offs, a recommendation) and wait for his
   decision. Code inside a method is Claude's call.
3. **Privacy first.** Monday records how a request moved, never what it carried: no bodies, query
   values, cookies, credentials, IPs or SQL parameter values. User ids are keyed hashes.
4. **Free tiers only** (Render, Neon, Cloudflare R2, GitHub Actions) until Rendee says otherwise.
5. **No secrets in the repo.** API keys come from the environment (`MONDAY_KEY`), never files or flags.

## Code

Clean code, SOLID, DDD. Dependencies point inward only:

| Project | Holds | May reference |
| --- | --- | --- |
| `Monday.Domain` | Aggregates, value objects, rules. No framework code | nothing |
| `Monday.Application` | Use cases and ports (interfaces) | Domain |
| `Monday.Infrastructure` | Port implementations (in-memory now, Postgres and R2 next) | Application, Domain |
| `Monday.Api` | Minimal API endpoints, auth, rate limits, error mapping | all above |
| `Monday.Indexer` | `monday-index`: Roslyn code graph of a repo | Domain |

- Rules live in the domain and are enforced there, so they hold whichever screen or API asks.
- Every domain rule has a test in `tests/Monday.Domain.Tests`.
- Warnings are errors; analyzers on `latest-recommended`.

## Commands

```bash
dotnet build                                            # whole solution (needs NuGet for the indexer)
dotnet run --project tests/Monday.Domain.Tests          # domain tests
dotnet run --project src/Monday.Api                     # API on Development (X-Monday-Dev-User header signs in)
dotnet run --project src/Monday.Indexer -- --root .     # writes .monday/graph.json
```

Offline (no NuGet), build the indexer against the SDK's own Roslyn:
`dotnet build src/Monday.Indexer -p:UseSdkRoslyn=true`.

The domain test project references `Monday.Api` (which uses the MCP SDK), so it needs NuGet too.

Viewer (`viewer/app`, Node >= 22.22.3):

```bash
npm test -- --watch=false                               # unit tests
npm run build                                           # builds into src/Monday.Api/wwwroot
npm run e2e                                             # Playwright against local ng serve, API stubbed
```

## Design

Read `design/TASTE.md` before any UI work. It is not optional.
All values come from `design/tokens.css`. Never invent a hex, a px or a duration.
To check a screen, use the Playwright MCP (`.mcp.json`) or `/design-loop`, on local or development
URLs only — never production. Impeccable (`/impeccable audit`, `/impeccable polish`, …) must also stay
inside `tokens.css` and must not change layout unless asked.

## Monday watches itself

While it is being built, Monday is its own first project. After each working session, re-run the
indexer and record what was done as activity events (one line each, with the graph node ids touched),
so the Monday Graph shows the work.
