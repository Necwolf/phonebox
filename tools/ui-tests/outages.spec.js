const { test, expect } = require('@playwright/test');

// The demo's /api/outages mock (tools/pages/demo.js) is relative to the first request: power goes off 40 minutes
// later for three hours. lvivska-oblast is stale, queue 9.9 doesn't exist, dnipropetrovska-oblast has emergency
// outages, and with pb-demo-outages-frozen the schedule's "updated" never moves.
async function open(page, options, { frozen = false } = {}) {
  await page.clock.install();
  await page.addInitScript(({ options, frozen }) => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'outages-test', title: 'Outages', widgets: [
        { id: 'ou', type: 'outages', x: 0, y: 0, w: 2, h: 2, options },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
    if (frozen) localStorage.setItem('pb-demo-outages-frozen', '1');
  }, { options, frozen });
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
