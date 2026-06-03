import type { Provider } from './types/cloud';

export const HOURLY_RATES: Record<string, Record<string, number>> = {
  digitalocean: {
    's-2vcpu-2gb':  0.01786,
    's-2vcpu-4gb':  0.03571,
    's-4vcpu-8gb':  0.07143,
    's-4vcpu-16gb': 0.11905,
  },
};

export const DEFAULT_SKUS: Record<string, string> = {
  digitalocean: 's-4vcpu-8gb',
};

export interface CostInputs {
  provider: Provider | string;
  sku?: string;
  provisionedAt: string | null;
  priceHourlyUsd?: number | null;
}

export interface CostBreakdown {
  totalThisMonth: number | null;
  hours: number | null;
  sku: string | null;
  unavailable: boolean;
}

const HOURS_PER_MONTH = 24 * 30;

export interface ProjectedRates {
  monthly: number;
  hourly: number;
  sku: string;
}

export function projectedRates(provider: string): ProjectedRates | null {
  const rates = HOURLY_RATES[provider];
  const sku = DEFAULT_SKUS[provider];
  if (!rates || !sku) return null;
  const hourly = rates[sku] ?? 0;
  return {
    monthly: Math.round(hourly * HOURS_PER_MONTH * 100) / 100,
    hourly,
    sku,
  };
}

export function computeMonthCosts(inputs: CostInputs, now: Date = new Date()): CostBreakdown {
  const rates = HOURLY_RATES[inputs.provider];
  const defaultSku = DEFAULT_SKUS[inputs.provider];
  if (!rates || !defaultSku) {
    if (inputs.priceHourlyUsd != null) {
      const monthStart  = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
      const provisioned = inputs.provisionedAt ? new Date(inputs.provisionedAt) : null;
      const start       = provisioned && provisioned > monthStart ? provisioned : monthStart;
      const hours       = Math.max(0, (now.getTime() - start.getTime()) / 3_600_000);
      return {
        totalThisMonth: round2(hours * inputs.priceHourlyUsd),
        hours,
        sku: inputs.sku ?? null,
        unavailable: false,
      };
    }
    return { totalThisMonth: null, hours: null, sku: null, unavailable: true };
  }

  const sku  = inputs.sku ?? defaultSku;
  const rate = inputs.priceHourlyUsd ?? rates[sku] ?? 0;

  const monthStart  = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const provisioned = inputs.provisionedAt ? new Date(inputs.provisionedAt) : null;
  const start       = provisioned && provisioned > monthStart ? provisioned : monthStart;
  const hours       = Math.max(0, (now.getTime() - start.getTime()) / 3_600_000);
  const total       = hours * rate;

  return {
    totalThisMonth: round2(total),
    hours,
    sku,
    unavailable: false,
  };
}

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}
