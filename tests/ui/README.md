# Browser validation

Drives the Signalboard sample in a real browser. `smoke.mjs` covers sign-in, the interactive
dashboard, the Viewer refusal, and the cross-tenant boundary. `landing.mjs` covers the public page
at mobile, tablet, desktop, and wide viewports, including keyboard behavior, asset loading,
horizontal overflow, animation replay and completion, reduced motion, metadata, and the
anonymous sign-in surface. It also checks all three real product captures and exact clipboard
contents for the Docker setup commands.

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
npm run landing
```

Pass a directory to also write screenshots, which is how the ones in `docs/images/` were made:

```bash
node smoke.mjs ../../docs/images
```

Point it elsewhere with `SIGNALBOARD_URL`.

For local lab evidence rather than a CI performance claim, capture cold and warm desktop plus a
throttled-mobile LCP/CLS sample. The JSON retains its exact conditions and explicitly labels its
375px check as a CSS-reflow surrogate, not a desktop-browser 200% zoom result:

```bash
npm run lab-metrics -- ../../.codex/ui-validation/signalboard-lab
```

The Open Graph image is generated from the real hero rather than a separate mock:

```bash
npm run social-preview -- ../../docs/images/lakewright-social-preview.png
```

Because that command writes a public raster, follow the image-review and hash process in
[`CONTRIBUTING.md`](../../CONTRIBUTING.md) whenever its bytes change.
