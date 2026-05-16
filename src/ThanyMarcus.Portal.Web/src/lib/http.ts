import { fetchWithStepUp } from './stepUpClient';
import type { ApiProblem } from './types/errors';

export async function parseProblem(r: Response): Promise<ApiProblem | null> {
  if (r.ok) return null;
  const ct = r.headers.get('content-type') ?? '';
  if (!ct.includes('application/json')) return null;
  try {
    return (await r.clone().json()) as ApiProblem;
  } catch {
    return null;
  }
}

export const apiFetch = fetchWithStepUp;
