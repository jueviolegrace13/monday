# monday-index-ts

`monday-index` for TypeScript and JavaScript repos, Next.js aware. It reads a repo where it is (nothing is
moved or changed apart from the `--out` file) and writes the same graph file as the C# `monday-index`,
so Monday's ingest endpoint and the Monday Graph take it as is.

```bash
npm install                                   # once (typescript is the only dependency)
node src/cli.ts --root <repo> --name "<App>" [--out <file>] [--commit <sha>] [--upload <monday url>]
MONDAY_KEY=… node src/cli.ts --root <repo> --upload http://localhost:5257
npm test                                      # fixture tests (node:test)
npm run typecheck
```

Node >= 22.18 runs the TypeScript sources directly. The key only ever comes from `MONDAY_KEY`.

## What it maps

| Graph | From |
| --- | --- |
| Module, Feature | A package (in a monorepo) or a top folder (`server`, `components`); the folders inside are features. A leading `src/` is skipped |
| Type | Each file; classes; objects of functions (`export const tradeService = { … }`) |
| Method | Functions, arrow-function consts (also wrapped: `memo(…)`, `cache(…)`), class and object methods |
| Test | `test(…)` / `it(…)` in `*.test.*`, `*.spec.*`, `__tests__/`, named with their `describe` blocks |
| Endpoint | App router `route.ts` verbs (`GET /api/trades/{id}`), `page.tsx` (`GET /journal`), pages router, and server actions in `"use server"` files (`ACTION trade-actions/deleteTradeAction`) |
| Middleware | `middleware.ts` / `proxy.ts`, with its `matcher`, in front of every endpoint (`Next` edges) |
| Calls, Covers | Resolved by the type checker across files and path aliases, including JSX use of components |
| DependsOn, Implements | File imports; class `extends` / `implements` |

Guards on an endpoint are the checks its handler makes itself: `Auth:<call>` for `auth()`, `require…()` and
friends, `Auth:header:authorization` for a hand-checked secret header, `RateLimit:<call>` for a rate limiter.

Threat rules (same ids as the C# indexer where the risk is the same): `write-without-auth`,
`public-without-rate-limit`, `credential-route-without-rate-limit`, `sql-built-from-strings`
(`$queryRawUnsafe`, string-built `query`), `weak-hash`, `code-from-strings` (`eval`), `html-from-data`
(`dangerouslySetInnerHTML` without sanitizing in place) and `hardcoded-secret`. Pages are left out of the
endpoint rules: they only read, and the middleware decides who sees them.

Files come from `git ls-files` (so `.gitignore` applies), minus declaration files, build output and the
folders in `.monday/ignore`. Only script sources are read; `.env` files and settings never are.

Not yet: type aliases and interfaces as nodes, guards applied by the middleware per path, and a secret check
that fails open when its environment variable is missing.
