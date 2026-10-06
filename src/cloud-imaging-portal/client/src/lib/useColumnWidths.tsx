import { useState, type CSSProperties, type ReactElement } from 'react';
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
