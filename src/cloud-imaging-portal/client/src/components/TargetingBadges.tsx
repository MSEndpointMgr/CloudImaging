import { Badge } from './ui/badge.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { describeNames } from '../lib/regions.ts';
import type { PickerLocation } from './LocationPicker.tsx';

/**
 * One-line summary of what something targets: an outline badge with `allLabel`, or the first
 * location plus "+N more" with up to five more names in a tooltip. Shared by Wi-Fi networks and
 * setup profiles so both tables read the same way.
 */
export function TargetingBadges({ all, locationIds, locations, allLabel, editNoun }: {
  all: boolean;
  locationIds: string[];
  locations: PickerLocation[];
  /** "All locations" for Wi-Fi networks, "All devices" for setup profiles. */
  allLabel: string;
  /** What the operator edits to see every location, for example "network" or "profile". */
  editNoun: string;
}): React.ReactElement {
  if (all) return <Badge variant="outline">{allLabel}</Badge>;
  const names = locationIds
    .map((id) => locations.find((l) => l.locationId === id)?.name)
    .filter((n): n is string => Boolean(n))
    .sort((a, b) => a.localeCompare(b));
  if (names.length === 0) return <span className="text-sm text-muted-foreground">No locations</span>;
  const rest = names.slice(1);
  return (
    <div className="flex min-w-0 flex-nowrap items-center gap-1">
      <Badge variant="outline" className="min-w-0"><span className="truncate" title={names[0]}>{names[0]}</span></Badge>
      {rest.length > 0 && (
        <Tooltip className="shrink-0" content={`${describeNames(rest.slice(0, 5))}${rest.length > 5 ? ` and ${String(rest.length - 5)} more` : ''}. Edit the ${editNoun} to see and search all ${String(names.length)}.`}>
          <Badge variant="muted" tabIndex={0} className="shrink-0 whitespace-nowrap focus-visible:ring-2 focus-visible:ring-ring">+{rest.length} more</Badge>
        </Tooltip>
      )}
    </div>
  );
}
