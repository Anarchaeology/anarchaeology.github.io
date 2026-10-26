// Static preview server: node serve-mount.js ROOT PORT [/prefix/=DIR ...]
// Serves ROOT, except requests under /prefix/ which are served read-only from DIR.
const http = require('http'), fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]), port = +process.argv[3] || 8765;
const mounts = process.argv.slice(4).map(a => { const [p, d] = a.split('='); return [p, path.resolve(d)]; });
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.json': 'application/json',
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.png': 'image/png', '.bmp': 'image/bmp' };
http.createServer((req, res) => {
  let rel = decodeURIComponent(new URL(req.url, 'http://x').pathname), base = root;
  for (const [prefix, dir] of mounts) if (rel.startsWith(prefix)) { base = dir; rel = rel.slice(prefix.length - 1); break; }
  const file = path.join(base, rel === '/' ? 'index.html' : rel);
  if (!file.startsWith(base)) { res.writeHead(403); return res.end(); }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); return res.end('not found'); }
    res.writeHead(200, { 'Content-Type': types[path.extname(file).toLowerCase()] || 'application/octet-stream' });
    res.end(data);
  });
}).listen(port, '127.0.0.1', () => console.log(`serving ${root} on http://127.0.0.1:${port}`));
