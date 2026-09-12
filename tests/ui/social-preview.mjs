// Render the actual landing hero at the Open Graph aspect ratio.
// Changed image bytes require the repository's public-image review.
import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import { chromium } from 'playwright';

const base = process.env.SIGNALBOARD_URL ?? 'http://localhost:8080';
const output = process.argv[2];
if (!output) throw new Error('Pass the output PNG path.');
await mkdir(dirname(output), { recursive: true });

const browser = await chromium.launch();
try {
  const page = await browser.newPage({
    viewport: { width: 1200, height: 630 },
    reducedMotion: 'reduce',
  });
  await page.goto(base, { waitUntil: 'networkidle' });
  await page.addStyleTag({ content: `
    .nav-shell { min-height: 68px; }
    .nav-section, .hero-actions, .hero-footnote, .capability-strip { display: none !important; }
    .hero-grid { min-height: 562px; padding-block: 28px; gap: 3rem; }
    .hero h1 { font-size: 64px; margin-block: 20px; }
    .hero-deck { font-size: 16px; line-height: 1.6; }
    .tenant-scene { max-width: 485px; }
  ` });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: output });
} finally {
  await browser.close();
}
