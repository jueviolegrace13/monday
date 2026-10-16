# References

What Monday is designed toward. Claude opens everything in this folder before UI work.

## Design studies (from VoltAgent/awesome-design-md, MIT)

| File | Why it's here |
| --- | --- |
| `linear-DESIGN.md` | Near-black product canvas, one chromatic accent, dense hierarchy |
| `raycast-DESIGN.md` | Dark dev tool that treats the page like a product screenshot |
| `vercel-DESIGN.md` | Developer-platform restraint: ink, hairlines, mono for technical detail |
| `supabase-DESIGN.md` | Single green CTA, dense product UI mockups |

These describe marketing sites. Use their rules for type, spacing, borders and restraint, not
their layouts or brand colours. Monday's colours come from `../tokens.css`.

## Screenshots (Rendee to add)

Add 8–15 **full-page** screenshots of product screens you like, named by what you want from them:

```
01-graph-<tool>.png        code or dependency graphs
02-trace-<tool>.png        request traces, spans, waterfalls
03-coverage-<tool>.png     test coverage views
04-codeview-<tool>.png     code view with explanations alongside
05-live-<tool>.png         real-time monitors, live request lists
06-table-<tool>.png        dense tables done well
00-avoid-<reason>.png      one you dislike; negative examples matter
```

Good sources for this kind of UI: Datadog and Honeycomb traces, Sentry issues, GitHub's code view and
Actions logs, Linear's issue list, Grafana panels, PostHog session replay. On a Mac, full-page capture
in Chrome DevTools: Cmd+Shift+P, then "Capture full size screenshot".
