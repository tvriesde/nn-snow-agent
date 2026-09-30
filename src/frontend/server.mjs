import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), 'dist');
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.png': 'image/png', '.ico': 'image/x-icon', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
export function createServer(env = process.env) {
  return http.createServer(async (req, res) => {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
    res.setHeader('X-Frame-Options', 'DENY');
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { Allow: 'GET, HEAD' }); res.end(); return;
    }
    let pathname;
    try { pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname); }
    catch { res.writeHead(400); res.end('Invalid request path'); return; }
    const json = (body) => {
      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
      res.end(req.method === 'HEAD' ? undefined : JSON.stringify(body));
    };
    if (pathname === '/health/live') { json({ status: 'ok' }); return; }
    if (pathname === '/runtime-config.json') {
      json({ apiBaseUrl: env.API_BASE_URL || '', entraTenantId: env.ENTRA_TENANT_ID || '', entraClientId: env.ENTRA_CLIENT_ID || '', entraApiScope: env.ENTRA_API_SCOPE || '' }); return;
    }
    if (pathname.includes('\0') || pathname.includes('\\') || pathname.split('/').includes('..')) {
      res.writeHead(400); res.end('Invalid request path'); return;
    }
    let file = path.join(root, pathname);
    try {
      if (!(await stat(file)).isFile()) throw new Error('Not a file');
    } catch {
      // Only page routes fall back. Missing assets and API paths must not return HTML.
      if (path.extname(pathname) || pathname.startsWith('/api/')) {
        res.writeHead(404); res.end('Not found'); return;
      }
      file = path.join(root, 'index.html');
    }
    try {
      const data = await readFile(file);
      res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control': path.extname(file) === '.html' ? 'no-cache' : 'public, max-age=3600' });
      res.end(req.method === 'HEAD' ? undefined : data);
    } catch { res.writeHead(503); res.end('Frontend export is missing. Run npm run build.'); }
  });
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT || 8080);
  createServer().listen(port, '0.0.0.0', () => console.log(`Frontend listening on port ${port}`));
}
