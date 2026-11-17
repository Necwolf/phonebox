const { test, expect } = require('@playwright/test');

// The demo's /api/outages mock (tools/pages/demo.js) is relative to the first request: power goes off 40 minutes
// later for three hours. lvivska-oblast is stale, queue 9.9 doesn't exist, dnipropetrovska-oblast has emergency
// outages, and with pb-demo-outages-frozen the schedule's "updated" never moves.
async function open(page, options, { frozen = false, w = 2, h = 2 } = {}) {
  await page.clock.install();
  await page.addInitScript(({ options, frozen, w, h }) => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'outages-test', title: 'Outages', widgets: [
        { id: 'ou', type: 'outages', x: 0, y: 0, w, h, options },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
    if (frozen) localStorage.setItem('pb-demo-outages-frozen', '1');
  }, { options, frozen, w, h });
  await page.goto('/panel/');
  return page.locator('[data-id="outages-test"] .w-outages');
}

test('counts down to the next power cut', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  await expect(w.locator('.ou-v')).toHaveText(/^(39|40):\d\d$/);
  await expect(w.locator('.ou-n')).toHaveText(/^Off \d\d:\d\d–\d\d:\d\d$/);
  await expect(w).toHaveClass(/\bon\b/);
});

test('switches to the power-on countdown inside the window', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  await page.clock.fastForward('00:41:00');
  await expect(w.locator('.ou-l')).toHaveText('Until power on');
  await expect(w.locator('.ou-v')).toHaveText(/^2:5\d$/);
  await expect(w.locator('.ou-n')).toHaveText(/^Power on at \d\d:\d\d$/);
  await expect(w).toHaveClass(/\boff\b/);
});

test('a stale region shows no countdown', async ({ page }) => {
  const w = await open(page, { region: 'lvivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-n')).toHaveText('Schedule not updated');
  await expect(w.locator('.ou-v')).toHaveText('—');
  await expect(w).toHaveClass(/\bnodata\b/);
});

test('an unknown queue shows no countdown', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '9.9' });
  await expect(w.locator('.ou-n')).toHaveText('No queue 9.9 here');
  await expect(w.locator('.ou-v')).toHaveText('—');
});

test('old data stops the countdown', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' }, { frozen: true });
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  await page.clock.fastForward('01:01:00');
  await expect(w.locator('.ou-n')).toHaveText('No fresh data');
  await expect(w.locator('.ou-v')).toHaveText('—');
});

test('emergency outages show a warning', async ({ page }) => {
  const w = await open(page, { region: 'dnipropetrovska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-warn')).toBeVisible();
  await expect(w.locator('.ou-warn')).toHaveAttribute('title', 'Emergency outages in effect');
});

test('the title defaults to region and queue', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-t')).toHaveText('Kyiv Region · 3.1');
});

test('fits a 2 × 2 slot', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-v')).toHaveText(/\d/);
  const box = await w.boundingBox();
  for (const sel of ['.ou-t', '.ou-l', '.ou-v', '.ou-n']) {
    const b = await w.locator(sel).boundingBox();
    expect(b, sel).not.toBeNull();
    expect(b.x, sel).toBeGreaterThanOrEqual(box.x - 0.5);
    expect(b.y, sel).toBeGreaterThanOrEqual(box.y - 0.5);
    expect(b.x + b.width, sel).toBeLessThanOrEqual(box.x + box.width + 0.5);
    expect(b.y + b.height, sel).toBeLessThanOrEqual(box.y + box.height + 0.5);
  }
});

test('a cut followed by an unknown schedule does not promise power at midnight', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '2.1' });
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  await page.clock.fastForward('00:41:00');
  await expect(w.locator('.ou-n')).toHaveText('Off · return time unknown');
  await expect(w.locator('.ou-v')).toHaveText('—');
  await expect(w).toHaveClass(/\boff\b/);
});

test('a gap in the schedule stops the countdown at its start', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '4.1' });
  await expect(w.locator('.ou-n')).toHaveText(/^Schedule unknown from \d\d:\d\d$/);
  await expect(w.locator('.ou-v')).toHaveText('—');
});

test('an unknown rest of the day is not "no outages today"', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '5.1' });
  await expect(w.locator('.ou-n')).toHaveText(/^Schedule unknown from \d\d:\d\d$/);
  await expect(w.locator('.ou-l')).not.toHaveText('No outages today');
});

test('losing the agent shows a warning while the last schedule still counts down', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  await expect(w.locator('.ou-warn')).toBeHidden();
  await page.evaluate(() => localStorage.setItem('pb-demo-outages-offline', '1'));
  await page.clock.fastForward('00:01:05');
  await expect(w.locator('.ou-warn')).toBeVisible();
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
});

test('the day strip dims when the data is no longer fresh', async ({ page }) => {
  await page.setViewportSize({ width: 780, height: 360 });
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' }, { frozen: true, h: 3, w: 4 });
  await expect(w.locator('.ou-strip .ou-seg').first()).toBeAttached();
  const opacity = () => w.locator('.ou-strip').evaluate((e) => Number(getComputedStyle(e).opacity));
  expect(await opacity()).toBe(1);
  await page.clock.fastForward('01:01:00');
  await expect(w.locator('.ou-n')).toHaveText('No fresh data');
  expect(await opacity()).toBeLessThan(1);
});

test('until power off is red, until power on is green', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  const color = () => w.locator('.ou-v').evaluate((e) => getComputedStyle(e).color);
  await expect(w.locator('.ou-l')).toHaveText('Until power off');
  expect(await color()).toBe('rgb(248, 113, 113)');
  await page.clock.fastForward('00:41:00');
  await expect(w.locator('.ou-l')).toHaveText('Until power on');
  expect(await color()).toBe('rgb(74, 222, 128)');
});
