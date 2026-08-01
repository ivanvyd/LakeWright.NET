# Browser smoke test

Drives the Signalboard sample in a real browser: sign-in, the interactive dashboard, the Viewer
refusal, and the cross-tenant boundary.

Deliberately not part of CI. [ADR 0007](../../docs/decisions/0007-blazor-for-the-sample-interface.md)
chose Blazor partly to keep a Node toolchain out of the build, and that still holds while running
this is opt-in.

It exists because the xUnit suite reads *prerendered* HTML. Prerendering and the interactive circuit
are two different renders of the same component, and the suite only ever saw the first. Two defects
lived in that gap until this ran: a Start button that did nothing when clicked before the circuit
connected, and a page that rendered, flashed `Loading.`, then rendered again on every visit.

## Run it

```bash
cd ../../samples/Signalboard && docker compose up -d
cd ../../tests/ui
npm install
npx playwright install chromium
node smoke.mjs
```

Pass a directory to also write screenshots, which is how the ones in `docs/images/` were made:

```bash
node smoke.mjs ../../docs/images
```

Point it elsewhere with `SIGNALBOARD_URL`.
