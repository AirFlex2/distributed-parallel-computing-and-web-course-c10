// Окремий автономний веб-сервер клієнта поза compose.yaml (§1.2 методичних вказівок).
// Запуск: node client/serve.mjs (відкриває клієнт на http://localhost:3000, який звертається до Nginx http://localhost:8080).

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const port = Number(process.env.CLIENT_PORT || 3000);

const mimeTypes = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'application/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.svg': 'image/svg+xml'
};

const server = http.createServer((req, res) => {
    const reqUrl = new URL(req.url || '/', `http://localhost:${port}`);
    const relativePath = reqUrl.pathname === '/' ? 'index.html' : reqUrl.pathname.replace(/^\/+/, '');
    const safePath = path.normalize(relativePath).replace(/^(\.\.(\/|\\|$))+/, '');
    const filePath = path.join(__dirname, safePath);

    if (!filePath.startsWith(__dirname)) {
        res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('403 Forbidden');
        return;
    }

    fs.readFile(filePath, (err, content) => {
        if (err) {
            res.writeHead(err.code === 'ENOENT' ? 404 : 500, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end(err.code === 'ENOENT' ? '404 Not Found' : '500 Internal Server Error');
            return;
        }
        const ext = path.extname(filePath).toLowerCase();
        res.writeHead(200, {
            'Content-Type': mimeTypes[ext] || 'application/octet-stream',
            'Cache-Control': 'no-store'
        });
        res.end(content);
    });
});

server.listen(port, '127.0.0.1', () => {
    console.log(`[С10 Standalone Client] Окремий клієнт запущено поза Docker Compose: http://localhost:${port}`);
    console.log(`[С10 Standalone Client] Цільовий балансувальник API (Nginx): http://localhost:8080`);
});
