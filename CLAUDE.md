# Monday

Monday watches any number of apps: their code graph, their security layers, their traffic, and what
Claude does while building them. For now it runs locally and assists engineers on their local
codebase only. Rendee decides; Claude builds.
The full plan is the "Monday — Plan" doc; decisions are logged in Sunday under `01-Projects/monday/`.

## Rules that are never bent

1. **Production is off limits.** For now Monday is used locally only. It never visits, watches or
   touches production: no connections, no monitoring, no code, data, config or infrastructure
   changes, no test requests. The domain refuses production environments, production API keys and
   data sent with them (`ProductionGuard.EnsureNotProduction`, `production.off_limits`), and every
   write path still calls `ProductionGuard.EnsureWritable`. Neither check has an override. Changes
   happen locally or in development, then reach production through Rendee's normal release.
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
| `tools/monday-index-ts` | `monday-index-ts`: the same graph file for TypeScript/JavaScript (Next.js), with the TypeScript compiler | nothing |

- Rules live in the domain and are enforced there, so they hold whichever screen or API asks.
- Every domain rule has a test in `tests/Monday.Domain.Tests`.
- Warnings are errors; analyzers on `latest-recommended`.

## Commands

```bash
dotnet build                                            # whole solution (needs NuGet for the indexer)
dotnet run --project tests/Monday.Domain.Tests          # domain tests
dotnet run --project src/Monday.Api                     # API on Development (X-Monday-Dev-User header signs in)
dotnet run --project src/Monday.Indexer -- --root .     # writes .monday/graph.json
node tools/monday-index-ts/src/cli.ts --root <ts repo>  # TypeScript/Next.js repos (npm install there once)
```

Offline (no NuGet), build the indexer against the SDK's own Roslyn:
`dotnet build src/Monday.Indexer -p:UseSdkRoslyn=true`.

The domain test project references `Monday.Api` (which uses the MCP SDK), so it needs NuGet too.

Viewer (`viewer/app`, Node >= 22.22.3):

```bash
npm test -- --watch=false                               # unit tests
npm run build                                           # builds into src/Monday.Api/wwwroot
npm run build -- --configuration development            # same, signed in as the dev user, for a local API-served run
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
