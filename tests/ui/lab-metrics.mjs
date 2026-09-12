// Opt-in local lab evidence. It is deliberately separate from the smoke suite: performance
// values are environment-dependent and should be retained with their raw conditions, never used
// as an unqualified CI gate.
import { chromium } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const base = process.env.SIGNALBOARD_URL ?? 'http://127.0.0.1:8080';
const output = resolve(process.argv[2] ?? '.codex/ui-validation/lab-metrics');
const captureMetrics = `
  window.__signalboardUiLab = { lcp: [], cls: 0, shifts: [] };
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) {
      window.__signalboardUiLab.lcp.push({ startTime: entry.startTime, size: entry.size, url: entry.url || null });
    }
  }).observe({ type: 'largest-contentful-paint', buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) {
      if (!entry.hadRecentInput) {
        window.__signalboardUiLab.cls += entry.value;
        window.__signalboardUiLab.shifts.push({ startTime: entry.startTime, value: entry.value });
      }
    }
  }).observe({ type: 'layout-shift', buffered: true });
`;

const browser = await chromium.launch();
const instrumentedContexts = new WeakSet();

async function measure(name, conditions, reuseContext = null) {
  const context = reuseContext ?? await browser.newContext({ viewport: conditions.viewport });
  if (!instrumentedContexts.has(context)) {
    await context.addInitScript(captureMetrics);
    instrumentedContexts.add(context);
  }
  const page = await context.newPage();
  let cdp;
  if (conditions.throttle) {
    cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', conditions.throttle);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  }
  const response = await page.goto(`${base}/`, { waitUntil: 'networkidle', timeout: 30000 });
  await page.waitForTimeout(3000); // Let the final LCP candidate and late layout shifts settle.
  const raw = await page.evaluate(() => {
    const navigation = performance.getEntriesByType('navigation')[0];
    const resources = performance.getEntriesByType('resource');
    return {
      observer: window.__signalboardUiLab,
      navigation: navigation && {
        type: navigation.type,
        domContentLoadedEventEnd: navigation.domContentLoadedEventEnd,
        loadEventEnd: navigation.loadEventEnd,
        transferSize: navigation.transferSize,
      },
      resourceTransferBytes: resources.reduce((total, resource) => total + resource.transferSize, 0),
      resourceCount: resources.length,
      layout: {
        clientWidth: document.documentElement.clientWidth,
        scrollWidth: document.documentElement.scrollWidth,
      },
    };
  });
  if (cdp) await cdp.detach();
  await page.close();
  if (!reuseContext) await context.close();
  const lcp = raw.observer.lcp.at(-1) ?? null;
  return { name, conditions, responseStatus: response?.status() ?? null, ...raw, lcpMs: lcp?.startTime ?? null };
}

const desktop = { viewport: { width: 1280, height: 800 }, cache: 'fresh context' };
const warmContext = await browser.newContext({ viewport: desktop.viewport });
const coldDesktop = await measure('cold-desktop', desktop, warmContext);
const warmDesktop = await measure('warm-desktop', { ...desktop, cache: 'same browser context after cold navigation' }, warmContext);
await warmContext.close();
const mobile = await measure('throttled-mobile', {
  viewport: { width: 375, height: 667 },
  cache: 'fresh context',
  throttle: { offline: false, downloadThroughput: 1_600_000 / 8, uploadThroughput: 750_000 / 8, latency: 150 },
});

// Playwright/CDP cannot control Chromium's desktop browser zoom. This is intentionally a CSS
// reflow surrogate, stricter than the effective 640 CSS-pixel width of a 1280px desktop at 200%.
const zoomSurrogate = await measure('css-reflow-surrogate-for-200-percent-zoom', {
  viewport: { width: 375, height: 667 },
  evidenceType: 'CSS reflow surrogate; not actual browser zoom',
  threshold: 'scrollWidth <= clientWidth + 1',
});

await browser.close();
const results = {
  capturedAtUtc: new Date().toISOString(),
  base,
  limitations: [
    'Local container lab metrics; they are not field Core Web Vitals and do not establish INP.',
    'LCP/CLS are captured by PerformanceObserver injected before navigation; no fabricated interaction metric is reported.',
    'The zoom check is a CSS reflow surrogate because this harness cannot set desktop browser zoom.',
  ],
  runs: [coldDesktop, warmDesktop, mobile, zoomSurrogate],
};
await mkdir(output, { recursive: true });
await writeFile(resolve(output, 'signalboard-ui-lab-metrics.json'), `${JSON.stringify(results, null, 2)}\n`);
for (const run of results.runs) {
  if (run.responseStatus !== 200 || run.layout.scrollWidth > run.layout.clientWidth + 1) {
    throw new Error(`${run.name} failed: ${JSON.stringify(run)}`);
  }
}
console.log(JSON.stringify(results, null, 2));
