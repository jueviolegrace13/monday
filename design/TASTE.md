# Taste

> Draft. The rules below describe the Monday Graph viewer as it already looks. Rendee owns this
> file: change anything that isn't what you want, and add your reference screenshots (see
> `references/README.md`). The references are what make this Monday's look and nobody else's.

## Read this before writing any UI

1. Open every image in `design/references/`. Look at them. Do not skip this.
2. Read `design/tokens.css`. Every value you use must come from it.
3. Read the `*-DESIGN.md` files in `design/references/` for component and state rules. They are
   studies of other dev tools: take their discipline (density, hierarchy, restraint), not their brand.

## What Monday looks like

- A developer instrument, not a marketing page. The code graph is the hero; chrome stays out of its way.
- Dark, near-black canvas. One green accent for actions, live state and selection.
- Dense but calm: small type on a strict scale, hairline borders, panels that float over the graph.
- Mono for anything a developer would copy (ids, paths, code, counts, durations).
- Data colours (node kinds, status, coverage) carry meaning. Chrome colours never compete with them.
- Every number, state or finding is explainable: a click shows where it came from.
- Motion explains a change of state (a node expanding, the camera moving to a selection). It never
  announces itself.

## Banned by default

- Gradient text and gradient backgrounds
- Emoji as icons
- Glow on text, panels or buttons (glow is for a live or selected indicator only)
- Colour as the only signal for status or coverage
- Drop shadows on anything not floating over the graph
- `border-radius` above 10px except on pills and status dots
- More than two font weights in one panel
- Font sizes off the scale in `tokens.css`
- Anything that shows a request's payload: bodies, query values, cookies, credentials, IPs, SQL
  parameter values (this is Monday's privacy rule, and it applies to the UI too)

If a brief genuinely needs one of these, say so and ask Rendee first.
