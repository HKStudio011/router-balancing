// Mock upstream OpenAI-compatible cho e2e slice 3A/3C.
// Nhận POST /v1/chat/completions (yêu cầu Bearer) và trả SSE echo — không phụ thuộc mạng thật.
// 3C: POST /__config {"failFirst":N} → N request chat kế trả 429 (retry-after: 5) rồi tự
// phục hồi — stateful fail N lần đầu để test failover/exhaustion/gate trên cùng mock.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 9999);
let failFirst = 0; // số request chat còn phải fail trước khi trở lại 200

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const body = Buffer.concat(chunks).toString('utf8');

    if (req.method === 'POST' && req.url === '/__config') {
      failFirst = Number(JSON.parse(body).failFirst ?? 0);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end('{}');
      return;
    }

    if (req.method !== 'POST' || !req.url?.includes('/v1/chat/completions')) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'not found' } }));
      return;
    }

    const auth = req.headers.authorization ?? '';
    if (!auth.startsWith('Bearer ') || auth.length <= 'Bearer '.length) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'missing bearer', type: 'invalid_request_error' } }));
      return;
    }

    if (failFirst > 0) {
      failFirst -= 1;
      res.writeHead(429, { 'content-type': 'application/json', 'retry-after': '5' });
      res.end(JSON.stringify({ error: { message: 'rate limited', type: 'rate_limit_error' } }));
      return;
    }

    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(`data: ${JSON.stringify({ echo: JSON.parse(body), key: auth.slice(7) })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`mock upstream listening on http://127.0.0.1:${PORT}`);
});
