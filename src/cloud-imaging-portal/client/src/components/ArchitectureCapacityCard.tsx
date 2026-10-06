import { CatalogStatTile } from './CatalogStatTile.tsx';
import { IMAGE_ARCHITECTURES, architectureLabel, type ImageArchitecture } from '../lib/wimMetadata.ts';

interface ArchitectureCapacityCardProps {
  /** Active entries per architecture. */
  counts: Record<ImageArchitecture, number>;
  max: number;
}

/**
 * Catalog capacity meters, one card per architecture, since capacity and "latest" are per
 * architecture.
 *
 * Slots are drawn as discrete pips rather than a percentage bar: the ceiling is small enough to
 * count, and "80%" reads as false precision for 4 of 5.
 */
export function ArchitectureCapacityCard({ counts, max }: ArchitectureCapacityCardProps): React.ReactElement {
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      {IMAGE_ARCHITECTURES.map(arch => {
        const used = counts[arch];
        const remaining = Math.max(0, max - used);
        const atCapacity = remaining === 0;
        const label = architectureLabel(arch);
        return (
          <CatalogStatTile
            key={arch}
            glyph={label}
            label={`${label} entries`}
            value={used}
            max={max}
            atCapacity={atCapacity}
            trailing={
              <>
                <div className="mb-1.5 flex justify-end gap-1.5" role="img" aria-label={`${used} of ${max} slots used`}>
                  {Array.from({ length: max }, (_, slot) => (
                    <span
                      key={slot}
                      className={[
                        'h-2 w-[26px] rounded-full',
                        slot >= used ? 'bg-muted' : atCapacity ? 'bg-amber-500' : 'bg-primary',
                      ].join(' ')}
                    />
                  ))}
                </div>
                <span
                  className={
                    atCapacity
                      ? 'text-xs font-medium text-amber-600 dark:text-amber-400'
                      : 'text-xs text-muted-foreground'
                  }
                >
                  {atCapacity
                    ? `At capacity, oldest ${label} entry is replaced next upload`
                    : `${remaining} slot${remaining === 1 ? '' : 's'} remaining`}
                </span>
              </>
            }
          />
        );
      })}
    </div>
  );
}
