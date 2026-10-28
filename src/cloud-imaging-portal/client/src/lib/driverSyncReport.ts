import {
  type DriverSyncItemOutcome, type DriverSyncReportRun, type DriverSyncRun, type DriverSyncRunItem, type DriverSyncTrigger, type DriverVendor,
} from './drivers.ts';

/** The trigger filter: Check for updates covers every model and one model alike. */
export type SyncTriggerFilter = '' | 'Scheduled' | 'Manual' | 'Check';

export interface SyncReportFilter {
  search: string;
  vendor: '' | DriverVendor;
  trigger: SyncTriggerFilter;
}

const CHANGES: readonly DriverSyncItemOutcome[] = ['Updated', 'New', 'NewWaitingForApproval', 'Withdrawn', 'Removed'];

function triggerMatches(trigger: DriverSyncTrigger, filter: SyncTriggerFilter): boolean {
  if (!filter) return true;
  return filter === 'Check' ? trigger === 'Check' || trigger === 'ModelCheck' : trigger === filter;
}

/** Vendors the run planned to read but never did; a running run may still read them. */
export function unreadCatalogs(run: DriverSyncRun): DriverVendor[] {
  return (run.plannedVendors ?? []).filter(planned => !run.vendors.some(v => v.vendor === planned));
}

function coversVendor({ run, items }: DriverSyncReportRun, vendor: DriverVendor): boolean {
  return (run.plannedVendors ?? []).includes(vendor) || run.vendors.some(v => v.vendor === vendor) || items.some(i => i.vendor === vendor);
}

/** Runs and their items narrowed by the filters. A search keeps only the runs with a matching model or component. */
export function filterSyncReport(runs: readonly DriverSyncReportRun[], filter: SyncReportFilter): DriverSyncReportRun[] {
  const needle = filter.search.trim().toLowerCase();
  const result: DriverSyncReportRun[] = [];
  for (const entry of runs) {
    if (!triggerMatches(entry.run.trigger, filter.trigger)) continue;
    if (filter.vendor && !coversVendor(entry, filter.vendor)) continue;
    const items = entry.items.filter(item =>
      (!filter.vendor || !item.vendor || item.vendor === filter.vendor)
      && (!needle || [item.modelName ?? '', item.componentName ?? ''].some(v => v.toLowerCase().includes(needle))));
    if (needle && items.length === 0) continue;
    const run = filter.vendor
      ? { ...entry.run, vendors: entry.run.vendors.filter(v => v.vendor === filter.vendor), plannedVendors: entry.run.plannedVendors?.filter(v => v === filter.vendor) }
      : entry.run;
    result.push({ run, items });
  }
  return result;
}

/** Counts per outcome of a run's items. */
export function itemCounts(items: readonly DriverSyncRunItem[]): Partial<Record<DriverSyncItemOutcome, number>> {
  const counts: Partial<Record<DriverSyncItemOutcome, number>> = {};
  for (const item of items) counts[item.outcome] = (counts[item.outcome] ?? 0) + 1;
  return counts;
}

export interface SyncReportTotals {
  runs: number;
  scheduled: number;
  byHand: number;
  finished: number;
  completed: number;
  partlyFailed: number;
  failed: number;
  averageMs: number | null;
  longestMs: number | null;
  updated: number;
  fresh: number;
  withdrawn: number;
  changed: number;
  downloadedComponents: number;
  downloadedBytes: number;
  componentFailures: number;
  failedComponents: number;
  catalogFailures: number;
  otherFailures: number;
}

/** The figures behind the report tiles. */
export function syncReportTotals(runs: readonly DriverSyncReportRun[]): SyncReportTotals {
  const totals: SyncReportTotals = {
    runs: runs.length, scheduled: 0, byHand: 0, finished: 0, completed: 0, partlyFailed: 0, failed: 0, averageMs: null, longestMs: null,
    updated: 0, fresh: 0, withdrawn: 0, changed: 0, downloadedComponents: 0, downloadedBytes: 0, componentFailures: 0, failedComponents: 0, catalogFailures: 0, otherFailures: 0,
  };
  const durations: number[] = [];
  const failedComponents = new Set<string>();
  for (const { run, items } of runs) {
    if (run.trigger === 'Scheduled') totals.scheduled++;
    else totals.byHand++;
    if (run.result !== 'Running') {
      totals.finished++;
      if (run.result === 'Completed') totals.completed++;
      else if (run.result === 'PartlyFailed') totals.partlyFailed++;
      else totals.failed++;
      if (run.finishedAt) durations.push(Date.parse(run.finishedAt) - Date.parse(run.startedAt));
    }
    totals.downloadedComponents += run.downloadedComponents ?? 0;
    totals.downloadedBytes += run.downloadedBytes ?? 0;
    totals.catalogFailures += run.vendors.filter(v => !v.processed).length + (run.result === 'Running' ? 0 : unreadCatalogs(run).length);
    for (const item of items) {
      if (item.outcome === 'Updated') totals.updated++;
      if (item.outcome === 'New' || item.outcome === 'NewWaitingForApproval') totals.fresh++;
      if (item.outcome === 'Withdrawn' || item.outcome === 'Removed') totals.withdrawn++;
      if (CHANGES.includes(item.outcome)) totals.changed++;
      if (item.outcome === 'Failed') {
        if (item.componentName) {
          totals.componentFailures++;
          failedComponents.add(item.componentName);
        } else {
          totals.otherFailures++;
        }
      }
    }
  }
  totals.failedComponents = failedComponents.size;
  if (durations.length > 0) {
    totals.averageMs = durations.reduce((a, b) => a + b, 0) / durations.length;
    totals.longestMs = Math.max(...durations);
  }
  return totals;
}

export interface WeekChanges {
  /** Monday of the week, `yyyy-mm-dd` in UTC. */
  week: string;
  updated: number;
  fresh: number;
  withdrawn: number;
  failed: number;
}

function mondayOf(date: Date): Date {
  const monday = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  monday.setUTCDate(monday.getUTCDate() - ((monday.getUTCDay() + 6) % 7));
  return monday;
}

/** Changes per week from the week of `from` to the week of `to`, empty weeks included. */
export function changesPerWeek(runs: readonly DriverSyncReportRun[], from: string, to: string): WeekChanges[] {
  const weeks = new Map<string, WeekChanges>();
  const last = mondayOf(new Date(`${to}T00:00:00Z`));
  for (let week = mondayOf(new Date(`${from}T00:00:00Z`)); week <= last; week.setUTCDate(week.getUTCDate() + 7)) {
    const key = week.toISOString().slice(0, 10);
    weeks.set(key, { week: key, updated: 0, fresh: 0, withdrawn: 0, failed: 0 });
  }
  for (const { run, items } of runs) {
    for (const item of items) {
      const bucket = weeks.get(mondayOf(new Date(item.at ?? run.startedAt)).toISOString().slice(0, 10));
      if (!bucket) continue;
      if (item.outcome === 'Updated') bucket.updated++;
      else if (item.outcome === 'New' || item.outcome === 'NewWaitingForApproval') bucket.fresh++;
      else if (item.outcome === 'Withdrawn' || item.outcome === 'Removed') bucket.withdrawn++;
      else if (item.outcome === 'Failed') bucket.failed++;
    }
  }
  return [...weeks.values()];
}

export interface RecurringFailure {
  componentName: string;
  modelName: string | null;
  failures: number;
  lastReason: string | null;
  lastAt: string;
}

/** Component failures grouped by component and model, most failures first. */
export function recurringFailures(runs: readonly DriverSyncReportRun[]): RecurringFailure[] {
  const groups = new Map<string, RecurringFailure>();
  for (const { run, items } of runs) {
    for (const item of items) {
      if (item.outcome !== 'Failed' || !item.componentName) continue;
      const at = item.at ?? run.startedAt;
      const key = `${item.componentName}\u0000${item.modelName ?? ''}`;
      const group = groups.get(key);
      if (!group) {
        groups.set(key, { componentName: item.componentName, modelName: item.modelName, failures: 1, lastReason: item.reason, lastAt: at });
        continue;
      }
      group.failures++;
      if (Date.parse(at) > Date.parse(group.lastAt)) {
        group.lastAt = at;
        group.lastReason = item.reason;
      }
    }
  }
  return [...groups.values()].sort((a, b) => b.failures - a.failures || Date.parse(b.lastAt) - Date.parse(a.lastAt));
}

export interface ItemGroup {
  key: string;
  modelName: string | null;
  release: DriverSyncRunItem['release'];
  vendor: DriverVendor | null;
  items: DriverSyncRunItem[];
}

/** A run's component items by model and release; downloads shared by several models form their own group. Run-level failures are left out. */
export function groupItems(items: readonly DriverSyncRunItem[]): ItemGroup[] {
  const groups = new Map<string, ItemGroup>();
  for (const item of items) {
    if (!item.modelId && !item.componentName) continue;
    const key = item.modelId ? `${item.modelId}|${item.release ?? ''}` : `downloads|${item.vendor ?? ''}`;
    const group = groups.get(key) ?? { key, modelName: item.modelId ? item.modelName : null, release: item.modelId ? item.release : null, vendor: item.vendor ?? null, items: [] };
    group.items.push(item);
    groups.set(key, group);
  }
  return [...groups.values()];
}
