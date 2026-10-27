import { useEffect, useMemo, useRef, useState } from 'react';
import { Check, Globe, Pencil, Plus, RotateCcw, Trash2, X } from 'lucide-react';
import { SaveBar } from './SaveBar.tsx';
import { unsavedRail, type UnsavedChange } from '../lib/unsavedChanges.ts';
import { Button, type ButtonStatus } from './ui/button.tsx';
import { UndoChangeButton } from './ui/unsaved-change.tsx';
import { cn } from '../lib/utils.ts';
import { Input } from './ui/input.tsx';
import { Select } from './ui/select.tsx';
import { SearchInput } from './ui/search-input.tsx';
import { CountPill } from './ui/count-pill.tsx';
import { EmptyState } from './ui/empty-state.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table.tsx';
import { ConfirmImpactDialog } from './ConfirmImpactDialog.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetch, extractErrorDetail } from '../lib/apiClient.ts';
import { countryName } from '../lib/countries.ts';
import {
  describeNames,
  regionChanges,
  regionCodeFor,
  toRegionsPayload,
  validateRegionCode,
  type CountryRegion,
  type RegionChange,
  type RegionDefinition,
  type RegionLocation,
  type RegionsView,
} from '../lib/regions.ts';

interface Props {
  view: RegionsView | null;
  loadError: string | null;
  locations: (RegionLocation & { effectiveRegion?: string | null })[];
  /** Reloads the mapping and the locations after a save. */
  onSaved: () => Promise<void>;
}

const NO_REGION = 'none';

/**
 * Regions tab: the tenant's regions and which region each country belongs to. Edits stay local
 * until Save, which confirms first when locations would change region.
 */
export function RegionsPanel({ view, loadError, locations, onSaved }: Props): React.ReactElement {
  const { notify } = useToast();
  const [regions, setRegions] = useState<RegionDefinition[]>([]);
  const [countries, setCountries] = useState<CountryRegion[]>([]);
  const [search, setSearch] = useState('');
  const [regionFilter, setRegionFilter] = useState('');
  const [changedOnly, setChangedOnly] = useState('all');
  const [renaming, setRenaming] = useState<{ regionId: string; code: string } | null>(null);
  const [adding, setAdding] = useState<string | null>(null);
  const [pendingChanges, setPendingChanges] = useState<RegionChange[] | null>(null);
  const [saveStatus, setSaveStatus] = useState<ButtonStatus>('idle');
  // Settles the promise a save returns once the region change confirmation is answered.
  const confirmed = useRef<((saved: boolean) => void) | null>(null);

  const draftPayload = JSON.stringify(toRegionsPayload(regions, countries));
  const draftRef = useRef(draftPayload);
  useEffect(() => { draftRef.current = draftPayload; });
  const lastView = useRef<RegionsView | null>(null);

  useEffect(() => {
    if (!view) return;
    const previous = lastView.current;
    lastView.current = view;
    // Locations reload the mapping after their own edits; an unsaved region edit survives that.
    if (previous && draftRef.current !== JSON.stringify(toRegionsPayload(previous.regions, previous.countries))) return;
    setRegions(view.regions.map(({ regionId, code }) => ({ regionId, code })));
    setCountries(view.countries);
  }, [view]);

  const savedPayload = useMemo(() => (view ? JSON.stringify(toRegionsPayload(view.regions, view.countries)) : ''), [view]);
  const isDirty = view !== null && draftPayload !== savedPayload;
  const discard = () => {
    if (!view) return;
    setRegions(view.regions.map(({ regionId, code }) => ({ regionId, code })));
    setCountries(view.countries);
  };

  const savedRegionOf = (countryCode: string) => view?.countries.find((c) => c.countryCode === countryCode)?.regionId ?? null;
  const savedCodeOf = (regionId: string) => view?.regions.find((r) => r.regionId === regionId)?.code ?? null;
  const changes: UnsavedChange[] = !view || !isDirty ? [] : [
    ...regions.filter((r) => savedCodeOf(r.regionId) !== r.code).map((r) => ({ label: `Region ${r.code}`, scope: 'Regions' })),
    ...view.regions.filter((s) => !regions.some((r) => r.regionId === s.regionId)).map((s) => ({ label: `Region ${s.code}`, scope: 'Regions' })),
    ...countries.filter((c) => c.regionId !== savedRegionOf(c.countryCode)).map((c) => ({ label: countryName(c.countryCode) ?? c.countryCode, scope: 'Regions' })),
  ];

  const regionOptions = useMemo(
    () => [...regions.map((r) => ({ value: r.regionId, label: r.code })), { value: NO_REGION, label: 'No region' }],
    [regions],
  );
  const codeOf = (regionId: string | null) => regions.find((r) => r.regionId === regionId)?.code ?? null;
  const locationsByCountry = useMemo(() => {
    const counts = new Map<string, number>();
    for (const l of locations) if (l.countryCode) counts.set(l.countryCode, (counts.get(l.countryCode) ?? 0) + 1);
    return counts;
  }, [locations]);

  const countryRows = useMemo(() => {
    const term = search.trim().toLowerCase();
    return countries
      .map((c) => ({ ...c, name: countryName(c.countryCode) ?? c.countryCode }))
      .filter((c) => !term || c.name.toLowerCase().includes(term) || c.countryCode.toLowerCase() === term)
      .filter((c) => !regionFilter || (regionFilter === NO_REGION ? c.regionId === null : c.regionId === regionFilter))
      .filter((c) => changedOnly === 'all' || c.regionId !== c.defaultRegionId)
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [countries, search, regionFilter, changedOnly]);

  const regionStats = (regionId: string, code: string) => ({
    countries: countries.filter((c) => c.regionId === regionId).length,
    locations: locations.filter((l) => (l.regionIsLegacy
      ? l.effectiveRegion?.toUpperCase() === code.toUpperCase()
      : regionCodeFor(regions, countries, l.countryCode) === code)).length,
  });

  const setCountryRegion = (countryCode: string, regionId: string | null) =>
    setCountries((current) => current.map((c) => (c.countryCode === countryCode ? { ...c, regionId } : c)));

  const commitRename = () => {
    if (!renaming || validateRegionCode(renaming.code, regions, renaming.regionId)) return;
    const code = renaming.code.trim().toUpperCase();
    setRegions((current) => current.map((r) => (r.regionId === renaming.regionId ? { ...r, code } : r)));
    setRenaming(null);
  };

  const commitAdd = () => {
    if (adding === null || validateRegionCode(adding, regions)) return;
    setRegions((current) => [...current, { regionId: crypto.randomUUID().replace(/-/g, ''), code: adding.trim().toUpperCase() }]);
    setAdding(null);
  };

  const removeRegion = (regionId: string) => {
    setRegions((current) => current.filter((r) => r.regionId !== regionId));
    setCountries((current) => current.map((c) => (c.regionId === regionId ? { ...c, regionId: null } : c)));
    if (regionFilter === regionId) setRegionFilter('');
  };

  // Confirms first when locations would change region; resolves once that is answered and saved.
  const save = (): Promise<boolean> => {
    if (!view) return Promise.resolve(false);
    const changes = regionChanges(
      locations,
      (country) => regionCodeFor(view.regions, view.countries, country),
      (country) => regionCodeFor(regions, countries, country),
    );
    if (changes.length === 0) return persist();
    return new Promise((resolve) => {
      confirmed.current = resolve;
      setPendingChanges(changes);
    });
  };

  const settle = (saved: boolean) => {
    confirmed.current?.(saved);
    confirmed.current = null;
  };

  const persist = async (): Promise<boolean> => {
    setSaveStatus('loading');
    try {
      const res = await apiFetch('/api/regions', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(toRegionsPayload(regions, countries)),
      });
      if (!res.ok) {
        setSaveStatus('error');
        notify({ status: 'error', title: 'Could not save regions', description: await extractErrorDetail(res, 'The regions were not saved.') });
        return false;
      }
      setPendingChanges(null);
      setSaveStatus('success');
      notify({ status: 'success', title: 'Regions saved.' });
      await onSaved();
      return true;
    } catch {
      setSaveStatus('error');
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
      return false;
    } finally {
      setTimeout(() => setSaveStatus('idle'), 1600);
    }
  };

  const changeImpact = (changes: RegionChange[]): string => {
    const moves = new Map<string, string[]>();
    for (const c of changes) {
      const key = `${c.from ?? 'no region'} to ${c.to ?? 'no region'}`;
      moves.set(key, [...(moves.get(key) ?? []), c.name]);
    }
    const lines = [...moves].map(([move, names]) => `${describeNames(names)} move from ${move}.`);
    return `${lines.join(' ')} Group tags for new Autopilot requests use the new region; devices already imported keep their tag.`;
  };

  if (loadError) return <p role="alert" className="text-sm text-destructive">{loadError}</p>;

  const noRegionCount = countries.filter((c) => c.regionId === null).length;
  const renameError = renaming ? validateRegionCode(renaming.code, regions, renaming.regionId) : null;
  const addError = adding !== null && adding.trim() ? validateRegionCode(adding, regions) : null;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-muted-foreground">
          Each country belongs to one region. Locations take their region from their country, and group tag templates use it as {'{Region}'}.
        </p>
        <Button variant="outline" onClick={() => setAdding('')} disabled={adding !== null || !view}>
          <Plus />
          Add region
        </Button>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {regions.map((r) => {
          const stats = regionStats(r.regionId, r.code);
          const isRenaming = renaming?.regionId === r.regionId;
          const savedCode = savedCodeOf(r.regionId);
          const regionChanged = view !== null && savedCode !== r.code;
          return (
            <div key={r.regionId} className={cn('rounded-xl border border-border bg-card px-5 py-4', unsavedRail(regionChanged))}>
              {isRenaming ? (
                <div className="space-y-2">
                  <div className="flex items-center gap-1">
                    <Input
                      aria-label={`New code for region ${r.code}`}
                      value={renaming.code}
                      maxLength={16}
                      autoFocus
                      aria-invalid={renameError !== null}
                      onChange={(e) => setRenaming({ regionId: r.regionId, code: e.target.value })}
                      onKeyDown={(e) => { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') setRenaming(null); }}
                      className="font-mono uppercase"
                    />
                    <Tooltip content="Apply">
                      <Button variant="ghost" size="icon" aria-label="Apply the new region code" disabled={renameError !== null} onClick={commitRename}><Check /></Button>
                    </Tooltip>
                    <Tooltip content="Cancel">
                      <Button variant="ghost" size="icon" aria-label="Cancel renaming" onClick={() => setRenaming(null)}><X /></Button>
                    </Tooltip>
                  </div>
                  {renameError && <p className="text-sm text-destructive">{renameError}</p>}
                </div>
              ) : (
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className="truncate font-mono text-sm font-semibold">{r.code}</p>
                    <p className="text-sm text-muted-foreground">
                      {stats.countries} {stats.countries === 1 ? 'country' : 'countries'}, {stats.locations} {stats.locations === 1 ? 'location' : 'locations'}
                    </p>
                  </div>
                  <div className="flex shrink-0 gap-1">
                    {regionChanged && (
                      <UndoChangeButton
                        label={`region ${r.code}`}
                        onUndo={() => (savedCode === null ? removeRegion(r.regionId) : setRegions((current) => current.map((x) => (x.regionId === r.regionId ? { ...x, code: savedCode } : x))))}
                      />
                    )}
                    <Tooltip content="Rename">
                      <Button variant="ghost" size="icon" aria-label={`Rename region ${r.code}`} onClick={() => setRenaming({ regionId: r.regionId, code: r.code })}><Pencil /></Button>
                    </Tooltip>
                    <Tooltip content="Delete. Its countries move to no region.">
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={`Delete region ${r.code}`}
                        className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                        onClick={() => removeRegion(r.regionId)}
                      >
                        <Trash2 />
                      </Button>
                    </Tooltip>
                  </div>
                </div>
              )}
            </div>
          );
        })}

        {adding !== null && (
          <div className="space-y-2 rounded-xl border border-border bg-card px-5 py-4">
            <div className="flex items-center gap-1">
              <Input
                aria-label="New region code"
                placeholder="e.g. LATAM"
                value={adding}
                maxLength={16}
                autoFocus
                aria-invalid={addError !== null}
                onChange={(e) => setAdding(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') commitAdd(); if (e.key === 'Escape') setAdding(null); }}
                className="font-mono uppercase"
              />
              <Tooltip content="Add">
                <Button variant="ghost" size="icon" aria-label="Add the region" disabled={!adding.trim() || addError !== null} onClick={commitAdd}><Check /></Button>
              </Tooltip>
              <Tooltip content="Cancel">
                <Button variant="ghost" size="icon" aria-label="Cancel adding a region" onClick={() => setAdding(null)}><X /></Button>
              </Tooltip>
            </div>
            {addError && <p className="text-sm text-destructive">{addError}</p>}
          </div>
        )}

        <div className="rounded-xl border border-dashed border-border px-5 py-4">
          <p className="text-sm font-semibold text-muted-foreground">No region</p>
          <p className="text-sm text-muted-foreground">{noRegionCount} {noRegionCount === 1 ? 'country' : 'countries'}</p>
        </div>
      </div>

      <div className="rounded-md border border-border overflow-hidden">
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
          <div className="flex items-center gap-2">
            <h3 className="text-sm font-semibold">Countries</h3>
            <CountPill count={countryRows.length} total={countries.length} />
          </div>
          <div className="flex-1" />
          <SearchInput value={search} onValueChange={setSearch} placeholder="Search countries" aria-label="Search countries" wrapperClassName="w-64" />
          <Select
            aria-label="Filter by region"
            value={regionFilter}
            onValueChange={setRegionFilter}
            options={regionOptions}
            placeholder="All regions"
            allowEmpty
            wrapperClassName="w-44"
          />
          <Select
            aria-label="Show"
            value={changedOnly}
            onValueChange={setChangedOnly}
            options={[{ value: 'all', label: 'All countries' }, { value: 'changed', label: 'Changed from default' }]}
            wrapperClassName="w-52"
          />
        </div>
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[28%]">Country</TableHead>
              <TableHead className="w-[10%]">Code</TableHead>
              <TableHead className="w-[24%]">Region</TableHead>
              <TableHead className="w-[14%]">Default</TableHead>
              <TableHead className="w-[12%]">Locations</TableHead>
              <TableHead className="w-[12%]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {!view ? null : countryRows.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="p-0">
                  <EmptyState icon={Globe} title="No countries match" description="Clear the search or the filters to see every country." />
                </TableCell>
              </TableRow>
            ) : countryRows.map((c) => {
              const changed = c.regionId !== c.defaultRegionId;
              const unsaved = c.regionId !== savedRegionOf(c.countryCode);
              return (
                <TableRow key={c.countryCode}>
                  <TableCell className={cn('font-medium', unsavedRail(unsaved))}>{c.name}</TableCell>
                  <TableCell className="font-mono text-sm">{c.countryCode}</TableCell>
                  <TableCell>
                    <Select
                      aria-label={`Region for ${c.name}`}
                      value={c.regionId ?? NO_REGION}
                      onValueChange={(v) => setCountryRegion(c.countryCode, v === NO_REGION ? null : v)}
                      options={regionOptions}
                    />
                  </TableCell>
                  <TableCell className="text-muted-foreground">{codeOf(c.defaultRegionId) ?? 'No region'}</TableCell>
                  <TableCell className="tabular-nums">{locationsByCountry.get(c.countryCode) ?? 0}</TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      {unsaved && <UndoChangeButton label={c.name} onUndo={() => setCountryRegion(c.countryCode, savedRegionOf(c.countryCode))} />}
                      {changed && (
                        <Tooltip content={`Reset to ${codeOf(c.defaultRegionId) ?? 'no region'}`}>
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label={`Reset ${c.name} to its default region`}
                            className="text-muted-foreground"
                            onClick={() => setCountryRegion(c.countryCode, c.defaultRegionId)}
                          >
                            <RotateCcw />
                          </Button>
                        </Tooltip>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>

      <SaveBar
        changes={changes}
        status={saveStatus}
        savedMessage="Regions saved."
        onDiscard={discard}
        onSave={save}
      />

      {pendingChanges && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `${String(pendingChanges.length)} ${pendingChanges.length === 1 ? 'location changes' : 'locations change'} region`,
            impact: changeImpact(pendingChanges),
            confirmLabel: 'Save',
            destructive: false,
          }}
          busy={saveStatus === 'loading'}
          onCancel={() => { setPendingChanges(null); settle(false); }}
          onConfirm={() => void persist().then(settle)}
          titleId="region-change-title"
        />
      )}
    </div>
  );
}
