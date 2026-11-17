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
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  await expect(w.locator('.ou-v')).toHaveText(/^(39|40):\d\d$/);
  await expect(w.locator('.ou-n')).toHaveText(/^Off \d\d:\d\d–\d\d:\d\d$/);
  await expect(w).toHaveClass(/\bon\b/);
});

test('switches to the power-on countdown inside the window', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  await page.clock.fastForward('00:41:00');
  await expect(w.locator('.ou-l')).toHaveText('On in');
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
  await expect(w.locator('.ou-l')).toHaveText('Off in');
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
  await expect(w.locator('.ou-l')).toHaveText('Off in');
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
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  await expect(w.locator('.ou-warn')).toBeHidden();
  await page.evaluate(() => localStorage.setItem('pb-demo-outages-offline', '1'));
  await page.clock.fastForward('00:01:05');
  await expect(w.locator('.ou-warn')).toBeVisible();
  await expect(w.locator('.ou-l')).toHaveText('Off in');
});

test('the schedule chart is drawn behind the text, also on a 2 × 2 tile', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-v')).toHaveText(/\d/);
  const chart = w.locator('canvas.ou-chart');
  await expect(chart).toBeVisible();
  const painted = () => chart.evaluate((c) => {
    const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
    let n = 0;
    for (let i = 3; i < d.length; i += 4) if (d[i]) n++;
    return n;
  });
  await expect.poll(painted).toBeGreaterThan(0);
  const [box, c] = [await w.boundingBox(), await chart.boundingBox()];
  expect(c.height).toBeGreaterThan(box.height * 0.3);
  expect(c.y + c.height).toBeGreaterThan(box.y + box.height - 2); // anchored to the bottom like a metric tile
});

test('the chart looks ahead, so a cut after midnight is on it', async ({ page }) => {
  // 23:30 in Kyiv: the mock's cut starts 40 minutes later, after midnight
  await page.clock.setFixedTime(new Date('2026-11-17T21:30:00Z'));
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  const red = () => w.locator('canvas.ou-chart').evaluate((c) => {
    const d = c.getContext('2d').getImageData(0, 0, c.width, Math.max(1, Math.floor(c.height * 0.3))).data; // the top: only cuts reach it
    let n = 0;
    for (let i = 0; i < d.length; i += 4) if (d[i + 3] && d[i] > 200 && d[i + 1] < 150) n++;
    return n;
  });
  await expect.poll(red).toBeGreaterThan(0);
});

test('the labels fit a 2 × 2 tile', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  expect(await w.locator('.ou-l').evaluate((e) => e.scrollWidth <= e.clientWidth)).toBe(true);
});

test('the chart dims when the data is no longer fresh', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' }, { frozen: true });
  const opacity = () => w.locator('canvas.ou-chart').evaluate((e) => Number(getComputedStyle(e).opacity));
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  const fresh = await opacity();
  await page.clock.fastForward('01:01:00');
  await expect(w.locator('.ou-n')).toHaveText('No fresh data');
  expect(await opacity()).toBeLessThan(fresh);
});

test('until power off is red, until power on is green', async ({ page }) => {
  const w = await open(page, { region: 'kiivska-oblast', queue: '3.1' });
  const color = () => w.locator('.ou-v').evaluate((e) => getComputedStyle(e).color);
  await expect(w.locator('.ou-l')).toHaveText('Off in');
  const label = () => w.locator('.ou-l').evaluate((e) => getComputedStyle(e).color);
  expect(await color()).toBe('rgb(248, 113, 113)');
  expect(await label()).toBe('rgb(248, 113, 113)');
  await page.clock.fastForward('00:41:00');
  await expect(w.locator('.ou-l')).toHaveText('On in');
  expect(await color()).toBe('rgb(74, 222, 128)');
  expect(await label()).toBe('rgb(74, 222, 128)');
});
