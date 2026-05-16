import { redirect } from '@sveltejs/kit';

export const ssr = false;
export const prerender = false;

export const load = async ({ fetch, url }) => {
  const r = await fetch('/api/auth/me');
  const me = r.ok ? await r.json() : null;

  if (me && me.totp === 'not-verified' && url.pathname !== '/totp-challenge') {
    throw redirect(302, '/totp-challenge');
  }

  return { me };
};
