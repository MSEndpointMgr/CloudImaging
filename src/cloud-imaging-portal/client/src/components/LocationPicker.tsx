import { useMemo, useState } from 'react';
import { Check, X } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { Checkbox } from './ui/checkbox.tsx';
import { SearchInput } from './ui/search-input.tsx';
import { Tooltip } from './ui/tooltip.tsx';

/** A location as the picker shows it. */
export interface PickerLocation {
  locationId: string;
  name: string;
  effectiveRegion?: string | null;
  countryCode?: string | null;
}

/**
 * Searchable checkbox list of locations, shared by Wi-Fi networks and setup profiles. Select all
 * and Clear act on the locations shown, so a search narrows them. A location with a
 * `disabledReason` cannot be picked and shows the reason in a tooltip.
 */
export function LocationPicker({ locations, selected, onChange, disabledReason, idPrefix = 'loc', footerNote }: {
  locations: PickerLocation[];
  selected: string[];
  onChange: (ids: string[]) => void;
  disabledReason?: (locationId: string) => string | undefined;
  /** Prefix for the row label ids, so two pickers on one page never share an id. */
  idPrefix?: string;
  /** Right-hand footer text while not searching, for example "2 targeted by other profiles". */
  footerNote?: string;
}): React.ReactElement {
  const [search, setSearch] = useState('');
  const term = search.trim().toLowerCase();
  const shown = useMemo(
    () => (term ? locations.filter((l) => [l.name, l.effectiveRegion, l.countryCode].some((v) => v?.toLowerCase().includes(term))) : locations),
    [locations, term],
  );
  const selectedSet = new Set(selected);
  const scope = term ? `the ${String(shown.length)} location${shown.length === 1 ? '' : 's'} shown` : 'every location';
  const pickable = shown.filter((l) => !disabledReason?.(l.locationId));

  const toggle = (id: string, on: boolean) => onChange(on ? [...selected, id] : selected.filter((s) => s !== id));
  const selectShown = () => onChange([...new Set([...selected, ...pickable.map((l) => l.locationId)])]);
  const clearShown = () => {
    const hide = new Set(shown.map((l) => l.locationId));
    onChange(selected.filter((id) => !hide.has(id)));
  };

  return (
    <div className="rounded-md border border-border">
      <div className="flex flex-wrap items-center gap-2 border-b border-border p-2">
        <SearchInput value={search} onValueChange={setSearch} placeholder="Search locations" aria-label="Search locations" wrapperClassName="min-w-40 flex-1" />
        <Tooltip content={`Select ${scope}`}>
          <Button variant="outline" onClick={selectShown} disabled={pickable.length === 0}><Check />Select all</Button>
        </Tooltip>
        <Tooltip content={`Clear ${scope}`}>
          <Button variant="outline" onClick={clearShown} disabled={shown.length === 0}><X />Clear</Button>
        </Tooltip>
      </div>
      <ul className="max-h-56 overflow-y-auto py-1" aria-label="Locations">
        {shown.length === 0 && <li className="px-3 py-2 text-sm text-muted-foreground">{locations.length === 0 ? 'No locations exist yet.' : 'No locations match.'}</li>}
        {shown.map((l) => {
          const labelId = `${idPrefix}-${l.locationId}`;
          const reason = disabledReason?.(l.locationId);
          const content = (
            <span className="flex min-w-0 flex-1 items-center gap-3">
              <Checkbox checked={selectedSet.has(l.locationId)} onCheckedChange={(on) => toggle(l.locationId, on)} labelledBy={labelId} disabled={Boolean(reason)} />
              <span id={labelId} className={`min-w-0 flex-1 truncate text-sm${reason ? ' text-muted-foreground' : ''}`}>
                {l.name}
                {reason && <span className="sr-only">. {reason}</span>}
              </span>
              <span className="shrink-0 text-xs text-muted-foreground">{[l.effectiveRegion, l.countryCode].filter(Boolean).join(', ')}</span>
            </span>
          );
          return (
            <li key={l.locationId} className="flex items-center px-3 py-1">
              {reason ? <Tooltip content={reason} className="min-w-0 flex-1">{content}</Tooltip> : content}
            </li>
          );
        })}
      </ul>
      <div className="flex justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
        <span>{selected.length} of {locations.length} selected</span>
        {term ? <span>{shown.length} shown</span> : footerNote && <span>{footerNote}</span>}
      </div>
    </div>
  );
}
