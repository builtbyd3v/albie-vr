// Daily cron (vercel.json): a free Hugging Face Space sleeps after 48 h with no traffic, so wake the cloud server once a day.
// Env: CLOUD_URL (https://<hf-user>-albie-cloud.hf.space), ALBIE_APP_KEY, CRON_SECRET (Vercel sends it as a Bearer token).
export async function GET(request: Request) {
  if (request.headers.get('authorization') !== `Bearer ${process.env.CRON_SECRET}`)
    return Response.json({ error: 'cron only' }, { status: 401 });
  const answer = await fetch(`${process.env.CLOUD_URL}/health`, { headers: { 'x-albie-key': process.env.ALBIE_APP_KEY ?? '' } });
  return Response.json({ status: answer.status });
}
