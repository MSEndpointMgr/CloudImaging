import { useMemo, useState } from 'react';
import { Eye, GripVertical, Pencil, Trash2 } from 'lucide-react';
import { Badge } from '../ui/badge.tsx';
import { Button } from '../ui/button.tsx';
import { Input } from '../ui/input.tsx';
import { SearchInput } from '../ui/search-input.tsx';
import { Select } from '../ui/select.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { TargetingBadges } from '../TargetingBadges.tsx';
import type { PickerLocation } from '../LocationPicker.tsx';
import { cn } from '../../lib/utils.ts';
import {
  SCRIPT_PHASE_SHORT_LABELS,
  effectiveScriptLocations,
  keyOf,
  locationFilterFooter,
  moveScriptBy,
  moveScriptTo,
  scriptsForLocation,
  timeoutLabel,
  type SetupProfileTargeting,
  type SetupScript,
} from '../../lib/setupProfiles.ts';

const ALL_SCRIPTS = '';

/**
 * The scripts card's table: search, a location filter that lists exactly what a device from that
 * location runs, and the run order changed in place by typing a number or dragging a row by its
 * handle (the arrow keys move a focused handle). Moving inserts and renumbers the phase.
 */
export function ScriptsTable({ scripts, targeting, locations, profileLocations, readOnly, onChange, onEdit, onDelete }: {
  /** Every script, in run order. */
  scripts: SetupScript[];
  targeting: SetupProfileTargeting;
  /** Every location in the catalog, for names. */
  locations: PickerLocation[];
  /** The locations this profile's scripts may run at, for the filter. */
  profileLocations: PickerLocation[];
  readOnly: boolean;
  onChange: (scripts: SetupScript[]) => void;
  /** Opens a script by its index in `scripts`. */
  onEdit: (index: number) => void;
  onDelete: (index: number) => void;
}): React.ReactElement {
  const [search, setSearch] = useState('');
  const [locationId, setLocationId] = useState(ALL_SCRIPTS);
  const [editingOrder, setEditingOrder] = useState<{ key: string; value: string } | null>(null);
  const [dragging, setDragging] = useState<string | null>(null);
  const [drop, setDrop] = useState<{ key: string; after: boolean } | null>(null);
  const allLocationIds = useMemo(() => locations.map((l) => l.locationId), [locations]);
  const filterLocation = profileLocations.find((l) => l.locationId === locationId);

  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    const base = filterLocation ? scriptsForLocation(scripts, filterLocation.locationId, targeting, allLocationIds) : scripts;
    return term ? base.filter((s) => s.name.toLowerCase().includes(term)) : base;
  }, [scripts, filterLocation, targeting, allLocationIds, search]);
  const filtered = visible.length !== scripts.length;
  const indexOf = (script: SetupScript) => scripts.indexOf(script);

  const commitOrder = (script: SetupScript, value: string) => {
    setEditingOrder(null);
    const order = Number(value);
    if (!Number.isInteger(order) || order < 1 || order === script.order) return;
    onChange(moveScriptTo(scripts, indexOf(script), order, filtered ? visible : undefined));
  };

  const dropOn = (target: SetupScript, after: boolean) => {
    const moving = scripts.find((s) => keyOf(s) === dragging);
    setDragging(null);
    setDrop(null);
    if (!moving || moving === target || moving.runs !== target.runs) return;
    const before = moving.order < target.order;
    const order = after ? (before ? target.order : target.order + 1) : (before ? target.order - 1 : target.order);
    onChange(moveScriptTo(scripts, indexOf(moving), order, filtered ? visible : undefined));
  };

  const filterOptions = [{ value: ALL_SCRIPTS, label: 'All scripts' }, ...profileLocations.map((l) => ({ value: l.locationId, label: l.name }))];
  const footer = filterLocation
    ? locationFilterFooter(visible.length, scripts.length, filterLocation.name)
    : filtered ? `${String(visible.length)} of ${String(scripts.length)} scripts` : `${String(scripts.length)} ${scripts.length === 1 ? 'script' : 'scripts'}`;

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <SearchInput value={search} onValueChange={setSearch} placeholder="Search scripts" aria-label="Search scripts" wrapperClassName="min-w-40 flex-1" />
        <Select id="scripts-location-filter" aria-label="Show the scripts for" value={locationId} onValueChange={setLocationId} options={filterOptions} wrapperClassName="w-[260px]" />
      </div>
      <div className="overflow-hidden rounded-md border border-border">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[88px]">Order</TableHead>
              <TableHead>Name</TableHead>
              <TableHead className="w-[240px]">Locations</TableHead>
              <TableHead className="w-[130px]">Runs</TableHead>
              <TableHead className="w-[90px] text-right">Timeout</TableHead>
              <TableHead className="w-[96px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {visible.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="text-muted-foreground">No scripts match.</TableCell>
              </TableRow>
            )}
            {visible.map((s) => {
              const key = keyOf(s);
              const effective = effectiveScriptLocations(s, targeting, allLocationIds);
              const dropHere = drop?.key === key ? drop : null;
              return (
                <TableRow
                  key={key}
                  data-testid={`script-row-${s.name}`}
                  className={cn(dragging === key && 'bg-primary/10', dropHere && (dropHere.after ? 'border-b-2 border-b-primary' : 'border-t-2 border-t-primary'))}
                  onDragOver={(e) => {
                    const moving = scripts.find((m) => keyOf(m) === dragging);
                    if (!moving || moving.runs !== s.runs || moving === s) return;
                    e.preventDefault();
                    const rect = e.currentTarget.getBoundingClientRect();
                    setDrop({ key, after: e.clientY > rect.top + rect.height / 2 });
                  }}
                  onDragLeave={() => setDrop((d) => (d?.key === key ? null : d))}
                  onDrop={(e) => { e.preventDefault(); dropOn(s, dropHere?.after ?? false); }}
                >
                  <TableCell>
                    <div className="flex items-center gap-1">
                      {!readOnly && (
                        <span
                          role="button"
                          tabIndex={0}
                          draggable
                          aria-roledescription="drag handle"
                          aria-label={`Move ${s.name}. Use the up and down arrow keys.`}
                          className="inline-flex cursor-grab rounded-sm text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                          onDragStart={(e) => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', key); setDragging(key); }}
                          onDragEnd={() => { setDragging(null); setDrop(null); }}
                          onKeyDown={(e) => {
                            if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
                            e.preventDefault();
                            onChange(moveScriptBy(scripts, indexOf(s), e.key === 'ArrowUp' ? -1 : 1, filtered ? visible : undefined));
                          }}
                        >
                          <GripVertical className="h-4 w-4" aria-hidden="true" />
                        </span>
                      )}
                      {editingOrder?.key === key ? (
                        <Input
                          autoFocus
                          aria-label={`Order of ${s.name}`}
                          inputMode="numeric"
                          value={editingOrder.value}
                          className="w-[44px] px-2 tabular-nums"
                          onChange={(e) => setEditingOrder({ key, value: e.target.value.replace(/\D/g, '') })}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') { e.preventDefault(); commitOrder(s, editingOrder.value); }
                            if (e.key === 'Escape') { e.preventDefault(); setEditingOrder(null); }
                          }}
                          onBlur={() => setEditingOrder(null)}
                        />
                      ) : readOnly ? (
                        <span className="tabular-nums">{s.order}</span>
                      ) : (
                        <Tooltip content="Type a new place in the run order">
                          <Button variant="ghost" size="icon" aria-label={`Change the order of ${s.name}, now ${String(s.order)}`} onClick={() => setEditingOrder({ key, value: String(s.order) })}>
                            <span className="tabular-nums">{s.order}</span>
                          </Button>
                        </Tooltip>
                      )}
                    </div>
                  </TableCell>
                  <TableCell><p className="truncate">{s.name}</p></TableCell>
                  <TableCell>
                    {effective !== null && effective.length === 0
                      ? <Badge variant="muted">No locations</Badge>
                      : <TargetingBadges all={effective === null} locationIds={effective ?? []} locations={locations} allLabel="All locations" editNoun="script" />}
                  </TableCell>
                  <TableCell>{SCRIPT_PHASE_SHORT_LABELS[s.runs]}</TableCell>
                  <TableCell className="text-right tabular-nums">{timeoutLabel(s.timeoutMinutes)}</TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      {readOnly ? (
                        <Tooltip content="View">
                          <Button variant="ghost" size="icon" aria-label={`View ${s.name}`} onClick={() => onEdit(indexOf(s))}><Eye /></Button>
                        </Tooltip>
                      ) : (
                        <>
                          <Tooltip content="Edit">
                            <Button variant="ghost" size="icon" aria-label={`Edit ${s.name}`} onClick={() => onEdit(indexOf(s))}><Pencil /></Button>
                          </Tooltip>
                          <Tooltip content="Delete">
                            <Button variant="ghost" size="icon" aria-label={`Delete ${s.name}`} className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" onClick={() => onDelete(indexOf(s))}>
                              <Trash2 />
                            </Button>
                          </Tooltip>
                        </>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
        <div className="flex justify-between gap-4 border-t border-border px-3 py-2 text-xs text-muted-foreground">
          <span>{footer}</span>
          <span>Order is the run order within each phase</span>
        </div>
      </div>
    </div>
  );
}
