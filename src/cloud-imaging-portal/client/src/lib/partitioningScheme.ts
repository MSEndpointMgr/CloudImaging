import type { UnsavedChange } from './unsavedChanges.ts';

export type PartitionType = 'EfiSystem' | 'Msr' | 'Windows' | 'Recovery';

export interface PartitionDefinition {
  partitionType: PartitionType;
  sizeMb: number;
  order: number;
}

export interface PartitioningScheme {
  partitions: PartitionDefinition[];
}

export const PARTITION_LABELS: Record<PartitionType, string> = {
  EfiSystem: 'EFI System Partition (ESP)',
  Msr: 'Microsoft Reserved (MSR)',
  Windows: 'Windows',
  Recovery: 'Recovery (WinRE)',
};

export const DEFAULT_SCHEME: PartitioningScheme = {
  partitions: [
    { partitionType: 'EfiSystem', sizeMb: 500, order: 0 },
    { partitionType: 'Msr', sizeMb: 16, order: 1 },
    { partitionType: 'Windows', sizeMb: 0, order: 2 },
    { partitionType: 'Recovery', sizeMb: 990, order: 3 },
  ],
};

export function sortedByOrder(scheme: PartitioningScheme): PartitionDefinition[] {
  return [...scheme.partitions].sort((a, b) => a.order - b.order);
}

export function schemeEquals(a: PartitioningScheme, b: PartitioningScheme): boolean {
  const pa = sortedByOrder(a);
  const pb = sortedByOrder(b);
  if (pa.length !== pb.length) return false;
  return pa.every((p, i) => p.partitionType === pb[i]?.partitionType && p.sizeMb === pb[i]?.sizeMb);
}

/** Position of a partition in the scheme's order. */
export function positionOf(scheme: PartitioningScheme, type: PartitionType): number {
  return sortedByOrder(scheme).findIndex((p) => p.partitionType === type);
}

/** The partitions whose size differs from the saved scheme, plus the order when it moved. */
export function partitioningChanges(scheme: PartitioningScheme, saved: PartitioningScheme, scope?: string): UnsavedChange[] {
  const changes: UnsavedChange[] = [];
  const current = sortedByOrder(scheme);
  const before = sortedByOrder(saved);
  for (const p of current) {
    const was = saved.partitions.find((s) => s.partitionType === p.partitionType);
    if (was && was.sizeMb !== p.sizeMb) changes.push({ label: `${PARTITION_LABELS[p.partitionType]} size`, scope });
  }
  if (current.some((p, i) => before[i]?.partitionType !== p.partitionType)) changes.push({ label: 'Partition order', scope });
  return changes;
}

/** Puts one partition back to its saved size and position; the others keep their relative order. */
export function undoPartition(scheme: PartitioningScheme, saved: PartitioningScheme, type: PartitionType): PartitioningScheme {
  const was = saved.partitions.find((s) => s.partitionType === type);
  const current = scheme.partitions.find((p) => p.partitionType === type);
  if (!was || !current) return scheme;
  const others = sortedByOrder(scheme).filter((p) => p.partitionType !== type);
  others.splice(positionOf(saved, type), 0, { ...current, sizeMb: was.sizeMb });
  return { partitions: others.map((p, i) => ({ ...p, order: i })) };
}
