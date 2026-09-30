import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from '../server.mjs';

test('NN-inspired light and dark action/text tokens meet normal-text contrast', async () => {
  const html = await readFile(new URL('../public/index.html', import.meta.url), 'utf8');
  const light = html.match(/:root\s*\{([^}]+)\}/)[1];
  const dark = html.match(/html\[data-theme="dark"\]\s*\{([^}]+)\}/)[1];
  const luminance = hex => {
    const channels = hex.match(/../g).map(value => parseInt(value, 16) / 255)
      .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  };
  for (const palette of [light, dark]) {
    const token = name => palette.match(new RegExp(`--cp-${name}:\\s*#([\\da-f]{6})`, 'i'))[1];
    for (const [foreground, background] of [['accent-fg', 'accent'], ['accent-text', 'bg'], ['accent-text', 'surface']]) {
      const values = [luminance(token(foreground)), luminance(token(background))];
      const ratio = (Math.max(...values) + 0.05) / (Math.min(...values) + 0.05);
      assert.ok(ratio >= 4.5, `${foreground} on ${background}: ${ratio.toFixed(2)}`);
    }
  }
});

test('production server health, public runtime configuration, assets and SPA fallback', async () => {
  const server = createServer({ API_BASE_URL: 'https://api.example.test', ENTRA_TENANT_ID: 'tenant', ENTRA_CLIENT_ID: 'spa', ENTRA_API_SCOPE: 'api://backend/Helpdesk.Access', SECRET_VALUE: 'must-not-leak' });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  try {
    const configResponse = await fetch(`${url}/runtime-config.json`);
    assert.equal(configResponse.headers.get('cache-control'), 'no-store');
    assert.deepEqual(await configResponse.json(), { apiBaseUrl: 'https://api.example.test', entraTenantId: 'tenant', entraClientId: 'spa', entraApiScope: 'api://backend/Helpdesk.Access' });
    assert.deepEqual(await (await fetch(`${url}/health/live`)).json(), { status: 'ok' });
    const root = await fetch(url);
    assert.equal(root.status, 200);
    const html = await root.text();
    assert.match(html, /NN · Employee IT Helpdesk Demo/);
    assert.match(html, /--cp-accent-text:/);
    assert.match(html, /--cp-brand:/);
    assert.match(html, /--cp-bg:/);
    assert.match(html, /<script[^>]+src=/);
    assert.equal(await (await fetch(`${url}/examples`)).text(), html);
    assert.equal(await (await fetch(`${url}/architecture`)).text(), html);
    assert.equal(await (await fetch(`${url}/architecture/`)).text(), html);
    const diagramResponse = await fetch(`${url}/architecture.excalidraw.json`);
    assert.equal(diagramResponse.status, 200);
    assert.match(diagramResponse.headers.get('content-type'), /json/);
    assert.equal((await diagramResponse.json()).type, 'excalidraw');
    assert.equal(await (await fetch(`${url}/auth/callback`)).text(), html);
    const script = html.match(/<script[^>]+src="([^"]+)"/)[1];
    const asset = await fetch(new URL(script, url));
    assert.equal(asset.status, 200);
    assert.match(asset.headers.get('content-type'), /javascript/);
    assert.equal((await fetch(`${url}/missing.js`)).status, 404);
    assert.equal((await fetch(`${url}/api/chat`)).status, 404);
    assert.equal((await fetch(`${url}/`, { method: 'POST' })).status, 405);
    assert.equal((await fetch(`${url}/%E0%A4%A`)).status, 400);
    const head = await fetch(`${url}/examples`, { method: 'HEAD' });
    assert.equal(head.status, 200); assert.equal(await head.text(), '');
    assert.equal(head.headers.get('x-frame-options'), 'DENY');
  } finally { await new Promise(resolve => server.close(resolve)); }
});
