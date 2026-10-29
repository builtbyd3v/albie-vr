// Shared checks for the cloud functions: the app key (x-albie-key vs env ALBIE_APP_KEY) and a per-IP rate limit.
// ponytail: the limit lives in memory per function instance, so it resets on a cold start and each instance counts
// alone. It stops one runaway headset or a casual script, not a determined attacker; use a shared store (KV) for that.
const hits = new Map<string, number[]>();

export function guard(request: Request, limit: number, windowS = 60): Response | null {
  const appKey = process.env.ALBIE_APP_KEY;
  if (appKey && request.headers.get('x-albie-key') !== appKey) {
    return Response.json({ error: 'unauthorized' }, { status: 401 });
  }
  const ip = (request.headers.get('x-forwarded-for') ?? '').split(',')[0].trim() || request.headers.get('x-real-ip') || 'unknown';
  const now = Date.now();
  const recent = (hits.get(ip) ?? []).filter((t) => now - t < windowS * 1000);
  if (recent.length >= limit) {
    hits.set(ip, recent);
    return Response.json({ error: 'rate limited, try again in a minute' }, { status: 429, headers: { 'retry-after': String(windowS) } });
  }
  recent.push(now);
  hits.set(ip, recent);
  if (hits.size > 5000) hits.clear(); // ponytail: crude memory cap
  return null;
}
