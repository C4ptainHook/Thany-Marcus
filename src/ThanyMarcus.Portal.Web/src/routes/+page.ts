import type { CloudStatusResponse } from '$lib/types/cloud';

export const load = async ({ parent, fetch }) => {
  const { me } = await parent();
  if (!me) return { me: null, cloud: null };

  const r = await fetch('/api/clouds/me');
  if (r.status === 404) return { me, cloud: null };
  if (!r.ok) return { me, cloud: null };
  const cloud = (await r.json()) as CloudStatusResponse;
  return { me, cloud };
};
