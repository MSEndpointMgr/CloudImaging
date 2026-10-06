import type { CSSProperties, ReactNode } from 'react';
import { ArrowUp, ArrowDown, ArrowUpDown } from 'lucide-react';
import { TableHead } from './table.tsx';
import type { SortState } from '../../lib/tableSort.ts';

interface SortableHeadProps<K extends string> {
  label: string;
  sortKey: K;
  sort: SortState<K>;
  onSort: (key: K) => void;
  className?: string;
  style?: CSSProperties;
  /** Extra header content, e.g. a `ColumnResizeHandle` (pass `relative` in `className`). */
  children?: ReactNode;
}

/** Clickable `<TableHead>` showing an up/down/neutral arrow for the currently active sort column. */
export function SortableHead<K extends string>({ label, sortKey, sort, onSort, className, style, children }: SortableHeadProps<K>): React.ReactElement {
  const active = sort.key === sortKey;
  const Icon = active ? (sort.dir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown;
  return (
    <TableHead className={className} style={style}>
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        // Tailwind's preflight resets `text-transform: none` on <button>, which would otherwise
        // override the `uppercase` class inherited from the parent <th> (see table.tsx) — reassert
        // it here so sortable headers render in the same ALL CAPS style as static ones.
        className="inline-flex items-center gap-1 uppercase hover:text-foreground focus-visible:outline-none"
      >
        {label}
        <Icon size={12} className={active ? '' : 'opacity-30'} />
      </button>
      {children}
    </TableHead>
  );
}
