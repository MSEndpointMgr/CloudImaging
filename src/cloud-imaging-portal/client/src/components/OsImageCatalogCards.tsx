import { CatalogStatTile } from './CatalogStatTile.tsx';
import { IMAGE_ARCHITECTURES, architectureLabel, type ImageArchitecture } from '../lib/wimMetadata.ts';

interface OsImageCatalogCardsProps {
  /** Active entries per architecture. */
  counts: Record<ImageArchitecture, number>;
  total: number;
  max: number;
}

/**
 * OS catalog totals, per architecture plus the overall ceiling.
 *
 * No slot meter here, unlike the boot and recovery catalogs: the OS ceiling is a single large
 * number enforced across both architectures as a hard reject, not a per-architecture rotation,
 * so there is no eviction to warn about and nothing countable to draw.
 */
export function OsImageCatalogCards({ counts, total, max }: OsImageCatalogCardsProps): React.ReactElement {
  const atCapacity = total >= max;
  return (
    <div className="grid gap-4 sm:grid-cols-3">
      {IMAGE_ARCHITECTURES.map(arch => {
        const label = architectureLabel(arch);
        return <CatalogStatTile key={arch} glyph={label} label={`${label} images`} value={counts[arch]} />;
      })}
      <CatalogStatTile
        glyph="ALL"
        label="Total images"
        value={total}
        max={max}
        atCapacity={atCapacity}
        trailing={
          atCapacity ? (
            <span className="text-xs font-medium text-amber-600 dark:text-amber-400">
              At capacity, remove an image before uploading
            </span>
          ) : undefined
        }
      />
    </div>
  );
}
