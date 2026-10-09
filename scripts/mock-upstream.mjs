// Mock upstream OpenAI-compatible cho e2e slice 3A/3C.
// Nhận POST /v1/chat/completions (yêu cầu Bearer) và trả SSE echo — không phụ thuộc mạng thật.
// 3C: POST /__config {"failFirst":N,"failStatus":S} → N request chat kế trả S (mặc định 429)
// rồi tự phục hồi — stateful fail N lần đầu để test failover/exhaustion/gate/transient backoff
// trên cùng mock. retry-after: 5 chỉ kèm 429; S khác (vd 504) không có header này.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 9999);
let failFirst = 0; // số request chat còn phải fail trước khi trở lại 200
let failStatus = 429; // status trả về khi fail — 429 giữ nguyên hành vi e2e failover cũ

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const body = Buffer.concat(chunks).toString('utf8');

    if (req.method === 'POST' && req.url === '/__config') {
      const cfg = JSON.parse(body);
      failFirst = Number(cfg.failFirst ?? 0);
      failStatus = Number(cfg.failStatus ?? 429);
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
      const headers = { 'content-type': 'application/json' };
      // retry-after: 5 chỉ hợp lệ với 429 — section failover e2e cũ phụ thuộc header này,
      // status transient (504) phải không có để đo backoff thuần
      if (failStatus === 429) headers['retry-after'] = '5';
      res.writeHead(failStatus, headers);
      const message = failStatus === 429 ? 'rate limited' : 'gateway timeout';
      const type = failStatus === 429 ? 'rate_limit_error' : 'api_error';
      res.end(JSON.stringify({ error: { message, type } }));
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
