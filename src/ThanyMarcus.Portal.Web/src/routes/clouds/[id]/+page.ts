import { error } from '@sveltejs/kit';
import { requireAuth } from '$lib/auth';
import type { CloudStatusResponse } from '$lib/types/cloud';

export const load = async ({ params, fetch, parent }) => {
  const { me } = await parent();
  requireAuth(me);
  const r = await fetch(`/api/clouds/${params.id}`);
  if (r.status === 404) throw error(404, 'Cloud not found');
  if (r.status === 403) throw error(403, "You don't own this cloud");
  if (!r.ok) throw error(r.status, 'Failed to load cloud status');
  const cloud = (await r.json()) as CloudStatusResponse;
  return { cloud };
};
