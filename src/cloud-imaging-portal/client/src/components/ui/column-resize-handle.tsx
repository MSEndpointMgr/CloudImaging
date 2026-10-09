import * as React from 'react';
import { cn } from '../../lib/utils.ts';

interface ColumnResizeHandleProps {
  /** Column name for the accessible label, e.g. "Serial / Session". */
  label: string;
  /** Current column width as a percentage of the table width. */
  widthPct: number;
  minPct: number;
  maxPct: number;
  onChange: (widthPct: number) => void;
  /** Double-click (or Home) restores the table's default widths. */
  onReset: () => void;
}

const KEYBOARD_STEP_PCT = 2;

/**
 * Drag handle on the right edge of a `<TableHead>` (which must be `relative`). Widths are percentages
 * so `table-fixed` rows keep summing to 100% instead of overflowing their container.
 */
export function ColumnResizeHandle({ label, widthPct, minPct, maxPct, onChange, onReset }: ColumnResizeHandleProps): React.ReactElement {
  const drag = React.useRef<{ startX: number; startPct: number; tableWidth: number } | null>(null);
  const clamp = (pct: number) => Math.min(maxPct, Math.max(minPct, pct));

  const handlePointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    const table = event.currentTarget.closest('table');
    if (!table) return;
    drag.current = { startX: event.clientX, startPct: widthPct, tableWidth: table.getBoundingClientRect().width };
    event.currentTarget.setPointerCapture(event.pointerId);
    event.preventDefault();
  };

  const handlePointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.tableWidth === 0) return;
    onChange(clamp(d.startPct + ((event.clientX - d.startX) / d.tableWidth) * 100));
  };

  const handlePointerUp = (event: React.PointerEvent<HTMLDivElement>) => {
    drag.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'ArrowRight') onChange(clamp(widthPct + KEYBOARD_STEP_PCT));
    else if (event.key === 'ArrowLeft') onChange(clamp(widthPct - KEYBOARD_STEP_PCT));
    else if (event.key === 'Home') onReset();
    else return;
    event.preventDefault();
  };

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-label={`Resize ${label} column`}
      aria-valuenow={Math.round(widthPct)}
      aria-valuemin={minPct}
      aria-valuemax={maxPct}
      tabIndex={0}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
      onDoubleClick={onReset}
      onKeyDown={handleKeyDown}
      className={cn(
        'group/resize absolute right-0 top-0 flex h-full w-2 cursor-col-resize touch-none justify-end',
        'focus-visible:outline-none',
      )}
    >
      <span
        aria-hidden="true"
        // Not `bg-border`: that is 20% lightness against a 22% `--table-header`, so the grip was
        // invisible until hovered. A foreground tint works on both themes without hardcoding.
        className={cn(
          'my-2 w-px bg-foreground/30 transition-all',
          'group-hover/resize:w-0.5 group-hover/resize:bg-foreground/70',
          'group-focus-visible/resize:w-0.5 group-focus-visible/resize:bg-ring',
        )}
      />
    </div>
  );
}
