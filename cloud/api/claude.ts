// Claude for the headset, so the Anthropic key never ships in the APK. Body: a Messages API request; the server keeps
// only the fields below, allows only the models below, and caps max_tokens. Answer: Anthropic's JSON, as is.
// Env: ANTHROPIC_API_KEY (required), ALBIE_APP_KEY (if set, the app must send it as x-albie-key).
import { guard } from '../lib/guard.js';

const MODELS = ['claude-sonnet-5-5', 'claude-opus-5-5'];
const MAX_TOKENS = 4096;
const FIELDS = ['system', 'messages', 'tools', 'tool_choice', 'stop_sequences', 'output_config', 'thinking'];

export async function POST(request: Request) {
  const blocked = guard(request, 30);
  if (blocked) return blocked;
  let body: Record<string, unknown>;
  try {
    const parsed: unknown = await request.json();
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) throw new Error();
    body = parsed as Record<string, unknown>;
  } catch {
    return Response.json({ error: 'the body must be a JSON object' }, { status: 400 });
  }
  if (!Array.isArray(body.messages) || body.messages.length === 0) {
    return Response.json({ error: 'messages is required' }, { status: 400 });
  }
  const model = typeof body.model === 'string' && MODELS.includes(body.model) ? body.model : MODELS[0];
  const asked = typeof body.max_tokens === 'number' && body.max_tokens > 0 ? Math.floor(body.max_tokens) : 1024;
  const out: Record<string, unknown> = { model, max_tokens: Math.min(asked, MAX_TOKENS) };
  for (const key of FIELDS) if (body[key] !== undefined) out[key] = body[key];

  const answer = await fetch('https://api.anthropic.com/v1/messages', {
    method: 'POST',
    headers: {
      'x-api-key': process.env.ANTHROPIC_API_KEY ?? '',
      'anthropic-version': '2026-10-21',
      'content-type': 'application/json',
    },
    body: JSON.stringify(out),
  });
  return new Response(await answer.text(), { status: answer.status, headers: { 'content-type': 'application/json' } });
}
