import { $, API, PANEL_HEADERS, ago, api, h, haptic, safeFetch, store, t, tick, toast } from '../main.js';
import { W } from '../registry.js';
import { cached, every, fmtNum, lines, pctSpan, sparkLine } from './helpers.js';

// ---------------------------------------------------------------- the internet
W.hackernews = {
  needs: ['tick'],
  create(box) {
    const list = h('div', 'hn-list', h('div', 'empty', t('w.loading')));
    box.style.setProperty('--c', '#ff6600');
    box.append(h('div', 'w-title', 'Hacker News', h('span', 'w-sub', '')), list);
    const load = every(15 * 60_000, async () => {
      try {
        const key = 'hn';
        let data = store.get('nc:' + key, null);
        if (!data || Date.now() - data.at > 15 * 60_000) {
          const ids = (await cached('hn-ids', 'https://hacker-news.firebaseio.com/v0/topstories.json', 15 * 60_000)).slice(0, 15);
          const items = await Promise.all(ids.map((id) => safeFetch(`https://hacker-news.firebaseio.com/v0/item/${id}.json`).then((r) => r.json()).catch(() => null)));
          data = { at: Date.now(), data: items.filter(Boolean).map((i) => ({ t: i.title, u: i.url, s: i.score, c: i.descendants || 0 })) };
          store.set('nc:' + key, data);
        }
        box.querySelector('.w-sub').textContent = ago(data.at);
        const n = Math.max(1, Math.floor((box.clientHeight - 30) / 33));
        list.replaceChildren(...data.data.slice(0, n).map((s, i) => {
          let host = '';
          try { host = s.u ? new URL(s.u).hostname.replace(/^www\./, '') : 'news.ycombinator.com'; } catch (_) {}
          return h('div', 'hn-i', h('span', 'hn-r', i + 1), h('div', 'hn-b', h('div', 'hn-t', s.t), h('div', 'hn-m', `▲ ${s.s} · ${s.c} comments · ${host}`)));
        }));
      } catch (_) { if (!list.querySelector('.hn-i')) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update: load };
  },
};

// Headlines from the RSS / Atom feeds set up on the PC. The agent fetches them (at most every 15 minutes); the
// phone gets titles, the feed's name and times, and opens an article on the PC by its id. The list scrolls, and an
// article opened from this phone loses its accent bar (the ids it opened are kept on the phone).
const FEEDS_READ = 'feeds-read', FEEDS_READ_MAX = 500, FEEDS_SHOWN = 60;
const feedsRead = () => new Set(store.get(FEEDS_READ, []));
const markFeedRead = (id) => {
  const ids = store.get(FEEDS_READ, []).filter((x) => x !== id);
  ids.push(id);
  store.set(FEEDS_READ, ids.slice(-FEEDS_READ_MAX));
};
W.feeds = {
  needs: ['tick'],
  create(box, { o }) {
    const only = String(o.feed || '').trim().toLowerCase();
    const showSource = o.showSource !== false, showTime = o.showTime !== false;
    const lineCount = ['2', '3'].includes(String(o.lines)) ? +o.lines : 1;
    const list = h('div', 'fd-list', h('div', 'empty', t('w.loading')));
    if (lineCount > 1) { list.classList.add('wrap'); list.style.setProperty('--fl', lineCount); }
    const sub = h('span', 'w-sub', '');
    box.style.setProperty('--c', '#f59e0b');
    if (o.title !== '') box.append(h('div', 'w-title', o.title || 'News', sub));
    box.append(list);
    let data = null, lastKey = '', busy = null;
    const open = async (it) => {
      if (!it.openable || busy) return;
      // this widget's own option, not just whether some widget allows it (the agent checks the latter)
      if (o.open !== true || !data?.canOpen) { toast(t('w.feeds.openOff'), 'err'); return; }
      haptic();
      busy = it.id;
      // one deadline for the request and its body, so a stalled answer can't leave the widget refusing taps
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 8000);
      try {
        const r = await fetch(`${API}/feeds/open?id=${encodeURIComponent(it.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
        const j = await r.json().catch(() => ({}));
        if (r.ok) { toast(t('w.feeds.opening'), 'ok'); markFeedRead(it.id); lastKey = ''; render(); }
        else toast(j.error || t('w.feeds.openFailed'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      finally { clearTimeout(timer); busy = null; }
    };
    const render = () => {
      const items = (data?.items || []).filter((i) => !only || i.source.toLowerCase() === only).slice(0, FEEDS_SHOWN);
      const canOpen = o.open === true && !!data?.canOpen;
      const read = feedsRead();
      const key = JSON.stringify([!!data, canOpen, items.map((i) => i.id + (read.has(i.id) ? '.' : '')), data?.errors, Math.floor(Date.now() / 60_000)]);
      if (key === lastKey) return;
      lastKey = key;
      const err = (data?.errors || []).filter((e) => !only || e.toLowerCase().startsWith(only + ':'));
      sub.textContent = err.length ? '⚠' : '';
      sub.title = err.join('\n');
      if (!data) return;
      if (!items.length) {
        list.replaceChildren(h('div', 'empty', err.length ? err[0] : data.updated && (data.items || []).length ? t('w.feeds.noneFrom', { name: o.feed }) : t('w.feeds.addInSettings')));
        return;
      }
      // the list is rebuilt every minute (the ages): keep where it was scrolled to
      const top = list.scrollTop;
      list.replaceChildren(...items.map((it) => {
        const meta = [showSource ? it.source : '', showTime && it.published ? ago(it.published) : ''].filter(Boolean).join(' · ');
        const link = it.openable && canOpen;
        const row = h('div', 'fd-i' + (link ? ' link' + (read.has(it.id) ? ' read' : '') : ''), h('div', 'fd-t', it.title), meta ? h('div', 'fd-m', meta) : null);
        row.onclick = () => open(it);
        return row;
      }));
      list.scrollTop = top;
    };
    const load = every(60_000, async () => {
      try { data = await api('/feeds', 'GET', 5000); lastKey = ''; render(); }
      catch (_) { if (!data) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update() { load(); render(); } };
  },
};

// Planned power outages in Ukraine for one region and queue. The agent fetches the schedule (at most every 15 minutes)
// and sends this queue's runs with Kyiv-time labels; the countdown runs here from those absolute times.
const OUTAGE_FRESH_MS = 60 * 60_000;
const fmtLeft = (ms) => {
  const s = Math.max(0, Math.floor(ms / 1000)), hrs = Math.floor(s / 3600), mins = Math.floor((s % 3600) / 60);
  return hrs ? `${hrs}:${String(mins).padStart(2, '0')}` : `${String(mins).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
};
W.outages = {
  needs: ['tick'],
  create(box, { o }) {
    const region = String(o.region || 'kiivska-oblast'), queue = String(o.queue || '1.1').trim();
    const title = h('div', 'ou-t', o.title || queue), warn = h('span', 'ou-warn', '⚠');
    const label = h('div', 'ou-l', ''), value = h('div', 'ou-v', '—'), note = h('div', 'ou-n', t('w.loading'));
    const strip = h('div', 'ou-strip');
    warn.hidden = true;
    box.append(h('div', 'ou-h', title, warn), label, value, note, strip);
    let data = null, lastOk = 0, stripKey = '';
    const load = every(60_000, async () => {
      try {
        data = await api(`/outages?region=${encodeURIComponent(region)}&queue=${encodeURIComponent(queue)}`, 'GET', 5000);
        lastOk = Date.now();
      } catch (_) { if (!data) note.textContent = t('w.offline'); return false; }
    });
    const show = (state, l, v, n) => {
      box.classList.toggle('on', state === 'on');
      box.classList.toggle('off', state === 'off');
      box.classList.toggle('nodata', state === 'nodata');
      label.textContent = l; value.textContent = v; note.textContent = n;
    };
    const drawStrip = (now) => {
      // the day's runs as a bar; redrawn when the data changes, the marker moves every minute
      const key = JSON.stringify([data.dayStart, data.runs.length, data.updated, Math.floor(now / 60_000)]);
      if (key === stripKey) return;
      stripKey = key;
      const span = data.dayEnd - data.dayStart;
      if (!(span > 0) || !data.runs.length) { strip.replaceChildren(); return; }
      const segs = data.runs.filter((r) => r.to > data.dayStart && r.from < data.dayEnd).map((r) => {
        const seg = h('i', 'ou-seg ' + r.state);
        const from = Math.max(r.from, data.dayStart), to = Math.min(r.to, data.dayEnd);
        seg.style.left = ((from - data.dayStart) / span * 100) + '%';
        seg.style.width = ((to - from) / span * 100) + '%';
        return seg;
      });
      const mark = h('i', 'ou-now');
      mark.style.left = (Math.min(1, Math.max(0, (now - data.dayStart) / span)) * 100) + '%';
      strip.replaceChildren(...segs, mark);
    };
    const render = () => {
      if (!data) return;
      const now = Date.now();
      const r = data.region;
      const name = r ? (window.PhoneBoxI18n?.getLang?.() === 'ru' ? r.nameRu : r.nameEn) || r.nameUa : '';
      title.textContent = o.title || (name ? `${name} · ${queue}` : queue);
      warn.hidden = !(r?.emergency || data.error);
      warn.title = r?.emergency ? t('w.outages.emergency') : data.error || '';
      strip.hidden = box.clientHeight < 110;
      if (!r) { show('nodata', '', '—', t('w.outages.noData')); return; }
      if (data.stale) { show('nodata', '', '—', t('w.outages.stale')); return; }
      if (!data.found) { show('nodata', '', '—', t('w.outages.notFound', { queue })); return; }
      if (!(now - data.updated <= OUTAGE_FRESH_MS && now - lastOk <= OUTAGE_FRESH_MS)) { show('nodata', '', '—', t('w.outages.noData')); return; }
      if (!strip.hidden) drawStrip(now);
      const cur = data.runs.find((x) => x.from <= now && now < x.to);
      if (!cur || cur.state === 'unknown') { show('nodata', '', '—', t('w.outages.noData')); return; }
      if (cur.state === 'off') { show('off', t('w.outages.untilOn'), fmtLeft(cur.to - now), t('w.outages.onAt', { time: cur.toLabel })); return; }
      const next = data.runs.find((x) => x.from >= cur.to && x.state === 'off');
      if (next) { show('on', t('w.outages.untilOff'), fmtLeft(next.from - now), t('w.outages.next', { from: next.fromLabel, to: next.toLabel })); return; }
      // nothing planned in the known schedule; a tomorrow without cuts may still be a placeholder, so say only what it says
      show('on', t('w.outages.noneToday'), '✓', t(data.tomorrow ? 'w.outages.noneTomorrow' : 'w.outages.tomorrowUnknown'));
    };
    return { update() { load(); render(); } };
  },
};

W.exchange = {
  needs: ['tick'],
  create(box, { o }) {
    const base = (o.base || 'EUR').toUpperCase();
    const syms = lines(o.symbols, ['TRY', 'USD', 'GBP']).map((s) => s.toUpperCase()).slice(0, 8);
    const list = h('div', 'fx-list', h('div', 'empty', t('w.loading')));
    box.append(h('div', 'w-title', `1 ${base}`, h('span', 'w-sub', '')), list);
    const load = every(60 * 60_000, async () => {
      try {
        const from = new Date(Date.now() - 31 * 864e5).toISOString().slice(0, 10);
        const d = await cached(`fx:${base}:${syms}`, `https://api.frankfurter.dev/v1/${from}..?base=${base}&symbols=${syms.join(',')}`, 60 * 60_000);
        const days = Object.keys(d.rates || {}).sort();
        if (!days.length) {
          box.querySelector('.w-sub').textContent = '';
          list.replaceChildren(h('div', 'empty', t('w.ratesUnavailable')));
          return false;
        }
        box.querySelector('.w-sub').textContent = 'ECB · ' + days[days.length - 1];
        list.replaceChildren(...syms.map((s) => {
          const series = days.map((k) => d.rates[k]?.[s]).filter((v) => Number.isFinite(v) && v > 0);
          const last = series[series.length - 1], prev = series[series.length - 2];
          const change = prev === undefined ? h('span', 'chg', '—') : pctSpan(((last - prev) / prev) * 100);
          const c = h('canvas', 'fx-sp');
          const row = h('div', 'fx-i', h('b', null, s), h('span', 'fx-v', fmtNum(last)), change, c);
          requestAnimationFrame(() => sparkLine(c, series, last >= series[0] ? '#22c55e' : '#ef4444'));
          return row;
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', t('w.ratesUnavailable'))); return false; }
    });
    return { update: load };
  },
};

W.crypto = {
  needs: ['tick'],
  create(box, { o }) {
    const coins = lines(o.coins, ['bitcoin', 'ethereum', 'solana']).map((s) => s.toLowerCase()).slice(0, 8);
    const cur = (o.currency || 'usd').toLowerCase();
    const list = h('div', 'fx-list', h('div', 'empty', t('w.loading')));
    box.append(h('div', 'w-title', 'Crypto', h('span', 'w-sub', cur.toUpperCase() + ' · 24 h · 7 d')), list);
    const load = every(5 * 60_000, async () => {
      try {
        const d = await cached(`cg:${cur}:${coins}`, `https://api.coingecko.com/api/v3/coins/markets?vs_currency=${cur}&ids=${coins.join(',')}&sparkline=true&price_change_percentage=24h`, 5 * 60_000);
        list.replaceChildren(...d.map((c) => {
          const cv = h('canvas', 'fx-sp');
          const series = c.sparkline_in_7d?.price || [];
          requestAnimationFrame(() => sparkLine(cv, series, (c.price_change_percentage_24h ?? 0) >= 0 ? '#22c55e' : '#ef4444'));
          return h('div', 'fx-i', h('b', null, (c.symbol || '').toUpperCase()), h('span', 'fx-v', fmtNum(c.current_price)), pctSpan(c.price_change_percentage_24h ?? 0), cv);
        }));
      } catch (_) { list.replaceChildren(h('div', 'empty', t('w.pricesUnavailable'))); return false; }
    });
    return { update: load };
  },
};

W.image = {
  needs: ['tick'],
  create(box, { o }) {
    const url = String(o.url || '');
    const img = h('img', 'im-img');
    img.alt = '';
    img.style.objectFit = o.fit === 'contain' ? 'contain' : 'cover';
    box.append(img);
    if (o.caption) box.append(h('div', 'im-cap', o.caption));
    if (!/^https?:\/\//i.test(url)) { box.replaceChildren(h('div', 'empty', t('w.setImageUrl'))); return {}; }
    img.onerror = () => box.classList.add('err');
    img.onload = () => box.classList.remove('err');
    const mins = Number(o.minutes) || 0;
    const load = every(mins > 0 ? mins * 60_000 : 1e12, () => { img.src = mins > 0 ? url + (url.includes('?') ? '&' : '?') + '_t=' + Date.now() : url; });
    return { update: load };
  },
};
