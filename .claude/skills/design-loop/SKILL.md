---
name: design-loop
description: Screenshot a running Monday screen with Playwright, score it against design/references and design/tokens.css, fix the single worst category, repeat. Use when Rendee asks to polish, audit or improve how a screen looks.
allowed-tools: Read, Edit, Glob, Grep, mcp__playwright__browser_navigate, mcp__playwright__browser_resize, mcp__playwright__browser_take_screenshot, mcp__playwright__browser_snapshot, mcp__playwright__browser_wait_for, mcp__playwright__browser_console_messages
---

# Design loop

Run a design loop on the URL Rendee gives (ask if none was given) until it passes or you hit 5 rounds.

**Never run this against production.** Local or development URLs only. Monday's production is
read-only, and that includes not sending it requests from a test browser.

## Setup, once

- Read `design/TASTE.md` and `design/tokens.css`.
- Open every image and `*-DESIGN.md` in `design/references/`. These are the standard. Match their
  discipline, not their brand.
- If the page is blank or shows an error, read the console messages and tell Rendee the error
  instead of scoring it.

## Each round

1. Navigate to the page. Wait for the main content (the graph canvas or the screen's heading) to be
   visible, then wait 300ms for fonts. Screenshot at 1440x900 and 390x844 into
   `design/screenshots/round-N-desktop.png` and `-mobile.png`.
2. Open both screenshots and look at them.
3. Score out of 5, with one specific sentence of evidence per score. No score without evidence.
   - **Type**: real hierarchy on the token scale, or many nearly equal sizes?
   - **Rhythm**: gaps on the space scale and consistent between panels?
   - **Density**: dense where data lives, calm around it? Is anything cramped or wasted?
   - **Restraint**: how many colours, weights, radii, glows and shadows are in play?
   - **Alignment**: does everything sit on a shared grid?
   - **Legibility of data**: can status, coverage and node kind be read without relying on colour
     alone, and does the graph stay the focus?
4. Take the single lowest score. Fix only that. Say what you are changing and which token you are
   changing it to before you edit.
5. Re-screenshot and re-score. If the score did not move, revert the change and try a different fix.
   Do not stack failed fixes.

Stop when every category is 4 or higher, or after 5 rounds. Then print a table of round-by-round
scores.

Do not add features, sections or copy. This loop only changes type, space, colour and shape. Before
finishing, grep your diff for raw values: any hex, px or ms outside `design/tokens.css` is a bug —
replace it with the nearest token, or tell Rendee there isn't one and stop.
