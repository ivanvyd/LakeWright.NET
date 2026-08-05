// Drives the Signalboard sample in a real browser. Deliberately not part of CI: ADR 0007 chose
// Blazor partly to keep a Node toolchain out of the build, and that still holds because running
// this is opt-in. It exists because the xUnit suite reads prerendered HTML and cannot tell whether
// the interactive circuit works — two defects hid behind that gap until this ran.
//
//   cd samples/Signalboard && docker compose up -d
//   cd ../../tests/ui && npm install && npx playwright install chromium
//   node smoke.mjs [screenshot-directory]
import { chromium } from 'playwright';

const BASE = process.env.SIGNALBOARD_URL ?? 'http://localhost:8080';
const OUT = process.argv[2];
const failures = [];
const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
  if (!ok) failures.push(name);
};

const shoot = (page, name) =>
  OUT ? page.screenshot({ path: `${OUT}/${name}`, fullPage: true }) : Promise.resolve();

const browser = await chromium.launch();

async function session(theme = 'light') {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 2,
    colorScheme: theme,
  });
  const page = await context.newPage();
  const consoleErrors = [];
  page.on('console', m => m.type() === 'error' && consoleErrors.push(m.text()));
  page.on('pageerror', e => consoleErrors.push(String(e)));
  return { context, page, consoleErrors };
}

async function signIn(page, name) {
  await page.goto(`${BASE}/signin`);
  await page.getByRole('button', { name: new RegExp(name, 'i') }).click();
  await page.waitForURL('**/operations');

  // Settle before doing anything: the page renders prerendered content first and the Start button
  // stays disabled until the circuit connects. Acting or screenshotting before that catches a
  // transient state rather than the page a visitor sees.
  await page.locator('.hint', { hasText: 'Connecting' })
    .waitFor({ state: 'detached', timeout: 20000 })
    .catch(() => {});
}

// ---- home, anonymous -------------------------------------------------------
{
  const { context, page, consoleErrors } = await session();
  await page.goto(BASE);
  check('home renders', await page.getByRole('heading', { level: 1 }).isVisible());
  // One read, one question. The previous form fetched the page twice and asked it through a
  // double negative, which also tripped CodeQL: `.includes('LakeWright.NET')` looks like a
  // hostname check to js/incomplete-url-substring-sanitization, because of the dot.
  check('home names the brand', (await page.content()).includes('LakeWright'));
  await shoot(page, `01-home.png`);

  await page.goto(`${BASE}/signin`);
  await shoot(page, `02-signin.png`);
  check('sign-in offers three people', (await page.locator('button.person').count()) === 3);
  check('no console errors on the anonymous pages', consoleErrors.length === 0, consoleErrors.join('; '));
  await context.close();
}

// ---- Alice: the interactive circuit ----------------------------------------
{
  const { context, page, consoleErrors } = await session();
  await signIn(page, 'Alice');
  check('dashboard is scoped to Acme', (await page.textContent('h1')).includes('Acme'));

  // Enabled only once the circuit is live, so waiting on it is waiting on interactivity.
  await page.waitForFunction(
    () => { const b = [...document.querySelectorAll('button')].find(x => x.textContent.trim() === 'Start');
            return b && !b.disabled; }, null, { timeout: 20000 });

  const before = await page.locator('table.operations tbody tr').count();
  await page.getByRole('button', { name: 'Start' }).click();

  // The row must appear without a navigation. That is the circuit doing its job; a Static SSR
  // page would need a reload and this wait would time out.
  const navigated = page.url();
  await page.waitForFunction(
    n => document.querySelectorAll('table.operations tbody tr').length > n,
    before, { timeout: 15000 }).catch(() => {});
  const after = await page.locator('table.operations tbody tr').count();

  check('starting an operation adds a row live', after > before, `${before} -> ${after}`);
  check('without navigating away', page.url() === navigated);
  check('no console errors on the dashboard', consoleErrors.length === 0, consoleErrors.join('; '));

  await shoot(page, `03-dashboard-admin.png`);

  // Dark theme, same page, because the stylesheet claims to handle both.
  await page.emulateMedia({ colorScheme: 'dark' });
  await shoot(page, `04-dashboard-dark.png`);

  // Narrow viewport: the table must scroll in its own box, not the page.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: 'light' });
  const overflows = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
  check('no horizontal page scroll at 390px', !overflows);
  await shoot(page, `05-dashboard-mobile.png`);
  await context.close();
}

// ---- Vera: a Viewer --------------------------------------------------------
{
  const { context, page, consoleErrors } = await session();
  await signIn(page, 'Vera');
  const denied = await page.locator('.denied').first().textContent();
  check('viewer is told why she cannot start', denied.includes('Member or above'));
  check('viewer still sees her organization\'s work',
    (await page.locator('table.operations tbody tr').count()) > 0);
  check('start control is disabled for a viewer',
    await page.getByRole('button', { name: 'Start' }).isDisabled());
  await shoot(page, `06-dashboard-viewer.png`);
  check('no console errors as viewer', consoleErrors.length === 0, consoleErrors.join('; '));
  await context.close();
}

// ---- Bob: the other tenant -------------------------------------------------
{
  const { context, page } = await session();
  await signIn(page, 'Bob');
  const body = await page.textContent('body');
  check('Bob sees Globex', (await page.textContent('h1')).includes('Globex'));
  check('Bob sees none of Acme\'s work', !body.includes('Acme'));
  await shoot(page, `07-dashboard-other-tenant.png`);
  await context.close();
}

await browser.close();
console.log(failures.length ? `\n${failures.length} FAILED` : '\nall checks passed');
process.exit(failures.length ? 1 : 0);
