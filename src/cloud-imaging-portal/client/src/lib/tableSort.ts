import { useState, useCallback } from 'react';

export type SortDir = 'asc' | 'desc';

export interface SortState<K extends string> {
  key: K;
  dir: SortDir;
}

/**
 * Generic column-sort state, seeded with an explicit initial column+direction (e.g. newest-first
 * for a "created" column). Clicking the already-active column flips its direction; clicking any
 * other column selects it ascending, the conventional default for a first click.
 */
export function useSort<K extends string>(initial: SortState<K>): [SortState<K>, (key: K) => void] {
  const [sort, setSort] = useState<SortState<K>>(initial);
  const toggle = useCallback((key: K) => {
    setSort(prev => (prev.key === key ? { key, dir: prev.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));
  }, []);
  return [sort, toggle];
}

/** Sorts a copy of `rows` by the accessor registered for the currently active sort column. */
export function sortRows<T, K extends string>(
  rows: T[],
  sort: SortState<K>,
  accessors: Record<K, (row: T) => string | number>,
): T[] {
  const accessor = accessors[sort.key];
  return [...rows].sort((a, b) => {
    const av = accessor(a);
    const bv = accessor(b);
    const cmp = typeof av === 'number' && typeof bv === 'number'
      ? av - bv
      : String(av).localeCompare(String(bv), undefined, { sensitivity: 'base' });
    return sort.dir === 'asc' ? cmp : -cmp;
  });
}
