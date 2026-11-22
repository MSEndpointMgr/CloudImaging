import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactElement } from 'react';
import { ColumnResizeHandle } from '../components/ui/column-resize-handle.tsx';

const MIN_COLUMN_PCT = 5;

/**
 * Percentage widths for the resizable columns of one table, in display order. Dragging a column's
 * edge trades width with its right-hand neighbour, so the row total (and `table-fixed` layout) never
 * changes; the last column therefore has no handle. Columns left out (expand toggles, fixed-px
 * actions) keep their own width.
 */
export function useColumnWidths<K extends string>(defaults: Readonly<Record<K, number>>) {
  const [widths, setWidths] = useState<Record<K, number>>({ ...defaults });
  const keys = Object.keys(defaults) as K[];

  const style = (key: K): CSSProperties => ({ width: `${widths[key]}%` });

  const handle = (key: K, label: string): ReactElement | null => {
    const next = keys[keys.indexOf(key) + 1];
    if (next === undefined) return null;
    return (
      <ColumnResizeHandle
        label={label}
        widthPct={widths[key]}
        minPct={MIN_COLUMN_PCT}
        maxPct={widths[key] + widths[next] - MIN_COLUMN_PCT}
        onChange={(pct) => setWidths(w => ({ ...w, [key]: pct, [next]: w[key] + w[next] - pct }))}
        onReset={() => setWidths({ ...defaults })}
      />
    );
  };

  return { style, handle };
}

/**
 * Like {@link useColumnWidths}, but every column starts at the width its data needs. The table is
 * first laid out automatically, with the `fit` columns as narrow as their content (give their cells
 * `whitespace-nowrap`) and the others sharing the rest; the measured widths then become the
 * percentages the edges trade, and a double-click goes back to them. `keys` are every column in
 * display order; keep both arrays stable (module constants).
 */
export function useFittedColumnWidths<K extends string>(keys: readonly K[], fit: readonly K[]) {
  const ref = useRef<HTMLTableElement>(null);
  const [state, setState] = useState<{ fitted: Record<K, number>; widths: Record<K, number> } | null>(null);

  // Runs before the first paint, so the automatic layout is never seen.
  useLayoutEffect(() => {
    const table = ref.current;
    const cells = table?.tHead?.rows[0]?.cells;
    if (state || !table || !cells) return;
    const total = table.getBoundingClientRect().width;
    // A table that is not laid out (hidden, or under jsdom) keeps the automatic layout.
    if (total === 0) return;
    const fitted = Object.fromEntries(keys.map((key, i) => [key, (cells[i].getBoundingClientRect().width / total) * 100])) as Record<K, number>;
    setState({ fitted, widths: fitted });
  }, [state, keys]);

  const style = (key: K): CSSProperties | undefined => {
    if (state) return { width: `${state.widths[key]}%` };
    return fit.includes(key) ? { width: '1px' } : undefined;
  };

  const handle = (key: K, label: string): ReactElement | null => {
    const next = keys[keys.indexOf(key) + 1];
    if (!state || next === undefined) return null;
    const { widths } = state;
    return (
      <ColumnResizeHandle
        label={label}
        widthPct={widths[key]}
        minPct={MIN_COLUMN_PCT}
        maxPct={widths[key] + widths[next] - MIN_COLUMN_PCT}
        onChange={(pct) => setState(s => s && { ...s, widths: { ...s.widths, [key]: pct, [next]: s.widths[key] + s.widths[next] - pct } })}
        onReset={() => setState(s => s && { ...s, widths: s.fitted })}
      />
    );
  };

  return { tableRef: ref, fixed: state !== null, style, handle };
}
