import { redirect } from '@sveltejs/kit';
import type { MeResponse } from './types/auth';

export function requireAuth(me: MeResponse | null): MeResponse {
  if (!me) throw redirect(302, '/api/auth/signin');
  return me;
}
