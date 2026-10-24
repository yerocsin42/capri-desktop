const sha256 = async (text) => {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
};

export default {
  async fetch(request, env, ctx) {
    const cors = {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
      'Access-Control-Max-Age': '86400'
    };
    const reply = (body, status = 200) => Response.json(body, { status, headers: { ...cors, 'Cache-Control': 'no-store' } });

    if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors });
    if (new URL(request.url).pathname !== '/star' || !['GET', 'POST'].includes(request.method)) return reply({ error: 'Not found' }, 404);

    if (request.method === 'POST') {
      const day = Math.floor(Date.now() / 86400000);
      const visitor = request.headers.get('CF-Connecting-IP') || 'local';
      const key = await sha256(visitor + ':' + day + ':' + env.SALT);
      const tap = await env.DB.prepare('INSERT OR IGNORE INTO taps (key, day) VALUES (?, ?)').bind(key, day).run();
      if (tap.meta.changes === 1) await env.DB.prepare('UPDATE stars SET count = count + 1').run();
      ctx.waitUntil(env.DB.prepare('DELETE FROM taps WHERE day < ?').bind(day).run());
    }

    const { count } = await env.DB.prepare('SELECT count FROM stars').first();
    return reply({ count });
  }
};
