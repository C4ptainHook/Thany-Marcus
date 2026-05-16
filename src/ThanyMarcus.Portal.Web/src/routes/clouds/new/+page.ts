import { requireAuth } from '$lib/auth';

export const load = async ({ parent }) => {
  const { me } = await parent();
  requireAuth(me);
  return {};
};
