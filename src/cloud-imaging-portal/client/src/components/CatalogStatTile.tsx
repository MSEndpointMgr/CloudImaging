import type { ReactNode } from 'react';

interface CatalogStatTileProps {
  /** Short code shown in the leading tile, e.g. "x64" or "ALL". */
  glyph: string;
  label: string;
  value: number;
  /** Renders "value / max". Omit for a plain count. */
  max?: number;
  /** Amber treatment for a catalog that can accept no more entries. */
  atCapacity?: boolean;
  /** Right-aligned meter area (slot pips, helper text). */
  trailing?: ReactNode;
}

/** One catalog statistic, styled as a standalone card. Shared so the image pages cannot drift apart. */
export function CatalogStatTile({
  glyph,
  label,
  value,
  max,
  atCapacity = false,
  trailing,
}: CatalogStatTileProps): React.ReactElement {
  return (
    <div className="flex items-center gap-3.5 rounded-xl border border-border bg-card px-5 py-4">
      {/* The architecture code doubles as the glyph, so no pictogram has to stand in for it. */}
      <span
        className={[
          'flex h-9 w-11 shrink-0 items-center justify-center rounded-md font-mono text-[11px] font-bold',
          atCapacity ? 'bg-amber-500/10 text-amber-600 dark:text-amber-400' : 'bg-primary/10 text-primary',
        ].join(' ')}
        aria-hidden="true"
      >
        {glyph}
      </span>

      <div>
        <p className="text-sm text-muted-foreground">{label}</p>
        <p className="mt-0.5 text-2xl font-semibold tabular-nums">
          {value}
          {max !== undefined && <span className="text-base font-normal text-muted-foreground"> / {max}</span>}
        </p>
      </div>

      {trailing !== undefined && (
        <>
          <div className="flex-1" />
          <div className="text-right">{trailing}</div>
        </>
      )}
    </div>
  );
}
