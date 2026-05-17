import type { Provider } from './types/cloud';

export const DigitalOceanCosts = {
  controlPlaneMonthly: 24,
  workerHourly: 0.125,
  typicalMonthlyMin: 28,
  typicalMonthlyMax: 32,
  coldStartMinutes: 5,
  warmWindowMinutes: 10,
} as const;

export const HOURLY_RATES: Record<string, Record<string, number>> = {
  digitalocean: {
    's-2vcpu-4gb':  0.03571,
    's-4vcpu-16gb': 0.11905,
  },
  hetzner: {
    'cpx21': 0.0119,
    'cpx41': 0.0524,
  },
};

export const DEFAULT_SKUS: Record<string, { controlPlane: string; worker: string }> = {
  digitalocean: { controlPlane: 's-2vcpu-4gb',  worker: 's-4vcpu-16gb' },
  hetzner:      { controlPlane: 'cpx21',        worker: 'cpx41'        },
};

export interface CostInputs {
  provider: Provider | string;
  controlPlaneSku?: string;
  workerSku?: string;
  provisionedAt: string | null;
  workerUptimeMonthSeconds: number | null;
}

export interface CostBreakdown {
  totalThisMonth: number | null;
  controlPlaneCost: number | null;
  workerCost: number | null;
  workerHours: number | null;
  controlPlaneHours: number | null;
  workerUnavailable: boolean;
}

const HOURS_PER_MONTH = 24 * 30;

export interface ProjectedRates {
  controlPlaneMonthly: number;
  controlPlaneSku: string;
  workerHourly: number;
  workerSku: string;
}

export function projectedRates(provider: string): ProjectedRates | null {
  const rates = HOURLY_RATES[provider];
  const skus = DEFAULT_SKUS[provider];
  if (!rates || !skus) return null;
  const cpRate = rates[skus.controlPlane] ?? 0;
  const wRate  = rates[skus.worker]       ?? 0;
  return {
    controlPlaneMonthly: Math.round(cpRate * HOURS_PER_MONTH * 100) / 100,
    controlPlaneSku:     skus.controlPlane,
    workerHourly:        wRate,
    workerSku:           skus.worker,
  };
}

export function computeMonthCosts(inputs: CostInputs, now: Date = new Date()): CostBreakdown {
  const rates = HOURLY_RATES[inputs.provider];
  const skus = DEFAULT_SKUS[inputs.provider];
  if (!rates || !skus) {
    return {
      totalThisMonth: null,
      controlPlaneCost: null,
      workerCost: null,
      workerHours: null,
      controlPlaneHours: null,
      workerUnavailable: true,
    };
  }

  const cpSku = inputs.controlPlaneSku ?? skus.controlPlane;
  const wSku  = inputs.workerSku       ?? skus.worker;
  const cpRate = rates[cpSku] ?? 0;
  const wRate  = rates[wSku]  ?? 0;

  const monthStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const provisioned = inputs.provisionedAt ? new Date(inputs.provisionedAt) : null;
  const cpStart = provisioned && provisioned > monthStart ? provisioned : monthStart;
  const cpHours = Math.max(0, (now.getTime() - cpStart.getTime()) / 3_600_000);
  const cpCost = cpHours * cpRate;

  if (inputs.workerUptimeMonthSeconds == null) {
    return {
      totalThisMonth: round2(cpCost),
      controlPlaneCost: round2(cpCost),
      workerCost: null,
      workerHours: null,
      controlPlaneHours: cpHours,
      workerUnavailable: true,
    };
  }

  const wHours = inputs.workerUptimeMonthSeconds / 3600;
  const wCost  = wHours * wRate;
  return {
    totalThisMonth: round2(cpCost + wCost),
    controlPlaneCost: round2(cpCost),
    workerCost: round2(wCost),
    workerHours: wHours,
    controlPlaneHours: cpHours,
    workerUnavailable: false,
  };
}

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}
