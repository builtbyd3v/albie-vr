// Short-lived voice token for the headset, so the xAI key never ships in the APK.
// Env: XAI_API_KEY (required), ALBIE_APP_KEY (if set, the app must send it as x-albie-key).
// ponytail: the app key lives in the APK, so it only stops casual scraping; add a rate limit before a public release.
export async function POST(request: Request) {
  const appKey = process.env.ALBIE_APP_KEY;
  if (appKey && request.headers.get('x-albie-key') !== appKey) {
    return Response.json({ error: 'unauthorized' }, { status: 401 });
  }
  const answer = await fetch('https://api.x.ai/v1/realtime/client_secrets', {
    method: 'POST',
    headers: { Authorization: `Bearer ${process.env.XAI_API_KEY}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ expires_after: { seconds: 300 } }),
  });
  if (!answer.ok) return Response.json({ error: `xAI answered ${answer.status}` }, { status: 502 });
  const { value, expires_at } = await answer.json();
  return Response.json({ value, expires_at });
}
