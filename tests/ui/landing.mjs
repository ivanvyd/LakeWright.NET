// Focused browser validation for the public landing surface. This complements smoke.mjs: it does
// not need a database, but it does exercise the responsive layout and progressive enhancements.
//
//   SIGNALBOARD_URL=http://127.0.0.1:8080 node landing.mjs [screenshot-directory]
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const BASE = process.env.SIGNALBOARD_URL ?? 'http://localhost:8080';
const OUT = process.argv[2];
const failures = [];

if (OUT) await mkdir(OUT, { recursive: true });

const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures.push(name);
};

const browser = await chromium.launch();
const devices = [
  ['mobile', 375, 667, true],
  ['tablet', 768, 1024, true],
  ['desktop', 1280, 800, false],
  ['wide', 1920, 1080, false],
];

for (const [name, width, height, isMobile] of devices) {
  const context = await browser.newContext({
    viewport: { width, height },
    colorScheme: 'light',
    hasTouch: isMobile,
    permissions: ['clipboard-read', 'clipboard-write'],
  });
  const page = await context.newPage();
  const consoleErrors = [];
  page.on('console', message => message.type() === 'error' && consoleErrors.push(message.text()));
  page.on('pageerror', error => consoleErrors.push(String(error)));
  page.on('requestfailed', request => consoleErrors.push(`${request.url()} — ${request.failure()?.errorText}`));

  const response = await page.goto(BASE, { waitUntil: 'networkidle' });
  check(`${name}: HTTP 200`, response?.status() === 200, String(response?.status()));
  check(`${name}: product is named in the H1`,
    (await page.getByRole('heading', { level: 1 }).innerText()).includes('Databricks.'));

  await page.locator('[data-motion-scene]').evaluate(async scene => {
    await Promise.all(scene.getAnimations({ subtree: true }).map(animation => animation.finished));
  });
  check(`${name}: all hero motion settles`,
    await page.locator('[data-motion-scene]').evaluate(scene =>
      scene.getAnimations({ subtree: true }).every(animation => animation.playState === 'finished')));
  check(`${name}: tab orientation follows its layout`,
    await page.getByRole('tablist').getAttribute('aria-orientation') ===
      (width > 520 && width <= 820 ? 'horizontal' : 'vertical'));

  await page.waitForFunction(() => document.querySelector('.hero').dataset.ambientRunning === 'true');
  const current = page.locator('.hero .current-front');
  check(`${name}: lake background is animated`,
    await current.evaluate(element => element.getAnimations().some(animation => animation.playState === 'running')));
  await page.getByRole('button', { name: 'Pause background motion' }).click();
  check(`${name}: background motion can be paused`,
    await current.evaluate(element => element.getAnimations().every(animation => animation.playState === 'paused')));
  await page.getByRole('button', { name: 'Resume background motion' }).click();
  check(`${name}: background motion resumes`,
    await current.evaluate(element => element.getAnimations().some(animation => animation.playState === 'running')));
  await page.evaluate(() => scrollTo({ top: 0, behavior: 'instant' }));

  if (OUT) await page.screenshot({ path: `${OUT}/landing-${name}-fold.png` });

  const productImage = page.locator('.product-frame img').first();
  await productImage.scrollIntoViewIfNeeded();
  await page.waitForFunction(() => document.querySelector('.hero').dataset.ambientRunning === 'false');
  check(`${name}: offscreen background stops`,
    await current.evaluate(element => element.getAnimations().every(animation => animation.playState === 'paused')));
  await page.waitForFunction(() => {
    const image = document.querySelector('.product-frame img');
    return image?.complete && image.naturalWidth > 0;
  });
  await productImage.evaluate(image => image.decode());
  await page.waitForTimeout(900);

  const layout = await page.evaluate(() => {
    const root = document.documentElement;
    const image = document.querySelector('.product-frame img');
    const imageBox = image.getBoundingClientRect();
    const imageStyle = getComputedStyle(image);
    return {
      viewport: root.clientWidth,
      scrollWidth: root.scrollWidth,
      overflowers: [...document.body.querySelectorAll('*')]
        .map(element => {
          const box = element.getBoundingClientRect();
          return { selector: `${element.tagName.toLowerCase()}.${element.className}`, right: box.right, width: box.width };
        })
        .filter(item => item.right > root.clientWidth + 1)
        .slice(0, 8),
      imageWidth: imageBox.width,
      imageCssWidth: imageStyle.width,
      imageMaxWidth: imageStyle.maxWidth,
      imageNaturalWidth: image.naturalWidth,
    };
  });
  check(`${name}: no horizontal page overflow`, layout.scrollWidth <= layout.viewport + 1,
    JSON.stringify(layout));
  check(`${name}: real product image loaded`, layout.imageNaturalWidth === 2560,
    `${layout.imageNaturalWidth}px`);
  check(`${name}: clean browser console`, consoleErrors.length === 0, consoleErrors.join('; '));

  if (name === 'desktop') {
    const replay = page.getByRole('button', { name: 'Replay the tenant architecture animation' });
    await replay.click();
    check('motion: replay restarts the architecture sequence',
      await page.locator('[data-motion-scene]').evaluate(scene =>
        scene.getAnimations({ subtree: true }).filter(animation => animation.playState === 'running').length >= 5));
    await page.locator('[data-motion-scene]').evaluate(async scene => {
      await Promise.all(scene.getAnimations({ subtree: true }).map(animation => animation.finished));
    });
    const canonicalUrl = new URL(BASE.endsWith('/') ? BASE : `${BASE}/`).href;
    const socialImageUrl = new URL('images/lakewright-social-preview.png', canonicalUrl).href;
    check('SEO: canonical targets the active host',
      await page.locator(`link[rel="canonical"][href="${canonicalUrl}"]`).count() === 1,
      canonicalUrl);
    check('SEO: Open Graph URLs target live local resources',
      await page.locator(`meta[property="og:url"][content="${canonicalUrl}"]`).count() === 1
      && await page.locator(`meta[property="og:image"][content="${socialImageUrl}"]`).count() === 1
      && await page.locator(`meta[name="twitter:image"][content="${socialImageUrl}"]`).count() === 1,
      socialImageUrl);
    check('SEO: Open Graph image has explicit dimensions',
      await page.locator('meta[property="og:image:width"][content="1200"]').count() === 1
      && await page.locator('meta[property="og:image:height"][content="630"]').count() === 1);
    const socialImage = await page.request.get(`${BASE}/images/lakewright-social-preview.png`);
    check('SEO: generated social image is served',
      socialImage.ok() && (await socialImage.body()).byteLength > 0,
      `${socialImage.status()}`);

    const semantics = await page.evaluate(() => {
      const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
      const headings = [...document.querySelectorAll('h1, h2, h3')]
        .map(element => Number(element.tagName.slice(1)));
      return {
        duplicateIds: ids.filter((id, index) => ids.indexOf(id) !== index),
        headingSkip: headings.some((level, index) => index > 0 && level > headings[index - 1] + 1),
        unnamedControls: [...document.querySelectorAll('a, button, select')]
          .filter(element => !element.getAttribute('aria-label') && !element.textContent.trim())
          .length,
      };
    });
    check('semantics: IDs are unique', semantics.duplicateIds.length === 0,
      semantics.duplicateIds.join(', '));
    check('semantics: heading levels do not skip', !semantics.headingSkip);
    check('semantics: interactive controls have names', semantics.unnamedControls === 0,
      String(semantics.unnamedControls));

    const tabs = page.getByRole('tab');
    check('boundary lab exposes three tabs', await tabs.count() === 3);
    await tabs.nth(0).focus();
    await page.keyboard.press('ArrowRight');
    check('ArrowRight selects Vera',
      await tabs.nth(1).getAttribute('aria-selected') === 'true'
      && await page.locator('#trace-vera').isVisible());
    await page.keyboard.press('End');
    check('End selects Bob',
      await tabs.nth(2).getAttribute('aria-selected') === 'true'
      && await page.locator('#trace-bob').isVisible());

    for (const [index, identity] of ['alice', 'vera', 'bob'].entries()) {
      await tabs.nth(index).click();
      const capture = page.locator(`#trace-${identity} img`);
      await capture.evaluate(image => image.decode());
      check(`${identity}: authentic capture loads when selected`,
        await capture.evaluate(image => image.naturalWidth === 2560));
    }

    const copy = page.getByRole('button', { name: 'Copy Signalboard setup commands' });
    await copy.click();
    await page.waitForFunction(() => ['Copied', 'Text selected'].includes(
      document.querySelector('[data-copy-target="quickstart-code"]')?.textContent.trim()));
    check('copy status is announced', (await page.locator('#copy-status').innerText()).length > 0);
    check('copy control writes the exact setup commands',
      (await page.evaluate(() => navigator.clipboard.readText())).replaceAll('\r\n', '\n').trim() ===
        'git clone https://github.com/ivanvyd/LakeWright.NET\ncd LakeWright.NET/samples/Signalboard\ndocker compose up');

    await page.goto(BASE, { waitUntil: 'networkidle' });
    await page.keyboard.press('Tab');
    check('skip link is the first keyboard stop',
      await page.evaluate(() => document.activeElement?.classList.contains('skip-link')));
    await page.keyboard.press('Enter');
    check('skip link moves focus to main',
      await page.evaluate(() => document.activeElement?.id === 'main-content'));

    if (OUT) {
      await page.evaluate(() => { document.activeElement?.blur(); scrollTo({ top: 0, behavior: 'instant' }); });
      await page.locator('[data-motion-scene]').evaluate(async scene => {
        await Promise.all(scene.getAnimations({ subtree: true }).map(animation => animation.finished));
      });
      await page.screenshot({ path: `${OUT}/landing-desktop-fold.png` });
      await page.locator('#proof').scrollIntoViewIfNeeded();
      await page.waitForFunction(() => {
        const image = document.querySelector('.product-frame img');
        return image?.complete && image.naturalWidth > 0;
      });
      await page.locator('.product-frame img').first().evaluate(image => image.decode());
      await page.waitForTimeout(700);
      await page.screenshot({ path: `${OUT}/landing-product-proof.png` });
      await page.locator('.product-frame').first().screenshot({ path: `${OUT}/landing-product-image.png` });
      await page.locator('#run').scrollIntoViewIfNeeded();
      await page.waitForTimeout(700);
      await page.screenshot({ path: `${OUT}/landing-quickstart.png` });
    }
  }

  if (OUT) {
    await page.evaluate(() => { document.activeElement?.blur(); scrollTo({ top: 0, behavior: 'instant' }); });
    await page.waitForTimeout(700);
    await page.screenshot({ path: `${OUT}/landing-${name}-full.png`, fullPage: true });
  }

  await context.close();
}

{
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    reducedMotion: 'reduce',
  });
  const page = await context.newPage();
  await page.goto(BASE, { waitUntil: 'networkidle' });
  const motion = await page.locator('.connection-pulse').evaluate(element => {
    const style = getComputedStyle(element);
    return { duration: style.animationDuration, behavior: getComputedStyle(document.documentElement).scrollBehavior };
  });
  check('reduced motion suppresses entrance animation', motion.duration === '0s', motion.duration);
  check('reduced motion disables smooth scrolling', motion.behavior === 'auto', motion.behavior);
  check('reduced motion hides the replay control',
    await page.getByRole('button', { name: 'Replay the tenant architecture animation' }).count() === 0);
  check('reduced motion disables the animated background',
    await page.locator('.current-front').first().evaluate(element => element.getAnimations().length === 0));
  await page.getByRole('tab').nth(1).click();
  check('reduced motion suppresses scripted transitions',
    await page.evaluate(() => document.getAnimations().filter(animation => animation.playState === 'running').length === 0));
  await context.close();
}

{
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const page = await context.newPage();
  await page.goto(`${BASE}/signin`, { waitUntil: 'networkidle' });
  check('sign-in renders all three real sample personas', await page.locator('button.person').count() === 3);
  const signInLayout = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
    overflowers: [...document.body.querySelectorAll('*')]
      .map(element => {
        const box = element.getBoundingClientRect();
        return { selector: `${element.tagName.toLowerCase()}.${element.className}`, right: box.right, width: box.width };
      })
      .filter(item => item.right > document.documentElement.clientWidth + 1)
      .slice(0, 8),
  }));
  check('sign-in has no horizontal overflow', signInLayout.scrollWidth <= signInLayout.viewport + 1,
    JSON.stringify(signInLayout));
  if (OUT) await page.screenshot({ path: `${OUT}/signin-mobile.png`, fullPage: true });
  await context.close();
}

await browser.close();
console.log(failures.length ? `\n${failures.length} FAILED` : '\nall landing checks passed');
process.exit(failures.length ? 1 : 0);
