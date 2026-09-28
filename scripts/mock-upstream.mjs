// Mock upstream OpenAI-compatible cho e2e slice 3A.
// Nhận POST /v1/chat/completions (yêu cầu Bearer) và trả SSE echo — không phụ thuộc mạng thật.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 9999);

const server = http.createServer((req, res) => {
  if (req.method !== 'POST' || !req.url?.includes('/v1/chat/completions')) {
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: { message: 'not found' } }));
    return;
  }

  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const auth = req.headers.authorization ?? '';
    if (!auth.startsWith('Bearer ') || auth.length <= 'Bearer '.length) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'missing bearer', type: 'invalid_request_error' } }));
      return;
    }

    const body = Buffer.concat(chunks).toString('utf8');
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(`data: ${JSON.stringify({ echo: JSON.parse(body), key: auth.slice(7) })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`mock upstream listening on http://127.0.0.1:${PORT}`);
});
