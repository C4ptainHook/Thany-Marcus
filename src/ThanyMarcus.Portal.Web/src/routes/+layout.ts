export const ssr = false;
export const prerender = false;

export const load = async ({ fetch }) => {
  const r = await fetch('/api/auth/me');
  return { me: r.ok ? await r.json() : null };
};
