import type { LucideIcon } from 'lucide-react';
import { Card, CardContent } from './ui/card';
import { IMAGE_ARCHITECTURES, architectureLabel, type ImageArchitecture } from '../lib/wimMetadata.ts';

interface ArchitectureCapacityCardProps {
  /** Active entries per architecture. */
  counts: Record<ImageArchitecture, number>;
  max: number;
  icon: LucideIcon;
}

/** Catalog capacity meters, one per architecture, since capacity and "latest" are per architecture. */
export function ArchitectureCapacityCard({ counts, max, icon: Icon }: ArchitectureCapacityCardProps): React.ReactElement {
  return (
    <Card>
      <CardContent className="grid gap-6 py-5 sm:grid-cols-2">
        {IMAGE_ARCHITECTURES.map(arch => {
          const used = counts[arch];
          const remaining = Math.max(0, max - used);
          const atCapacity = remaining === 0;
          const usedPct = Math.min(100, Math.round((used / max) * 100));
          const label = architectureLabel(arch);
          return (
            <div key={arch} className="flex items-center gap-4">
              <div className="flex items-center gap-3 sm:w-40">
                <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
                  <Icon className="h-5 w-5" aria-hidden="true" />
                </div>
                <div>
                  <p className="text-sm text-muted-foreground">{label} entries</p>
                  <p className="text-2xl font-semibold tabular-nums">
                    {used}<span className="text-base font-normal text-muted-foreground"> / {max}</span>
                  </p>
                </div>
              </div>
              <div className="flex-1">
                <div className="mb-2 flex items-center justify-between text-xs">
                  <span className={atCapacity ? 'font-medium text-amber-600 dark:text-amber-400' : 'text-muted-foreground'}>
                    {atCapacity
                      ? `At capacity. The oldest ${label} entry is replaced on the next ${label} upload`
                      : `${remaining} slot${remaining === 1 ? '' : 's'} remaining`}
                  </span>
                  <span className="tabular-nums text-muted-foreground">{usedPct}%</span>
                </div>
                <div className="h-2 w-full overflow-hidden rounded-full bg-muted">
                  <div
                    className={['h-full rounded-full transition-all', atCapacity ? 'bg-amber-500' : 'bg-primary'].join(' ')}
                    style={{ width: `${usedPct}%` }}
                  />
                </div>
              </div>
            </div>
          );
        })}
      </CardContent>
    </Card>
  );
}
