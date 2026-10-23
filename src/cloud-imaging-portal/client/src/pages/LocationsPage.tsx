import { useState, useEffect } from 'react';
import { Trash2, MapPin, Plus, Pencil, Check, X, Wifi, Upload } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Select } from '../components/ui/select.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { CountPill } from '../components/ui/count-pill.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import { LocationImportDialog } from '../components/LocationImportDialog.tsx';
import { RegionsPanel } from '../components/RegionsPanel.tsx';
import { LocationWifiDialog } from '../components/LocationWifiDialog.tsx';
import { useToast } from '../context/toastContext.tsx';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { countryName, countryOptions } from '../lib/countries.ts';
import { describeNames, regionCodeFor, type RegionsView } from '../lib/regions.ts';
import { networksForLocation, type WifiNetwork } from '../lib/wifiNetworks.ts';
import { fetchSetupProfiles, type SetupProfileSummary } from '../lib/setupProfiles.ts';
import { cn, formatDateTime } from '../lib/utils.ts';

interface LocationEntry {
  locationId: string;
  name: string;
  countryCode?: string | null;
  /** Region the location resolves to: from its country, or a region typed before that. */
  effectiveRegion?: string | null;
  /** True while a region typed before regions came from countries is still in use. */
  regionIsLegacy?: boolean;
  createdAt: string;
}

interface LocationDraft {
  name: string;
  countryCode: string;
}

type Tab = 'locations' | 'regions';

const TABS: { key: Tab; label: string }[] = [
  { key: 'locations', label: 'Locations' },
  { key: 'regions', label: 'Regions' },
];

const EMPTY_DRAFT: LocationDraft = { name: '', countryCode: '' };

function toPayload(draft: LocationDraft): Record<string, string | null> {
  return {
    name: draft.name.trim(),
    countryCode: draft.countryCode || null,
  };
}

/** Read-only region preview: the region follows from the chosen country. */
function RegionPreview({ id, region, hasCountry }: { id?: string; region: string | null; hasCountry: boolean }): React.ReactElement {
  return (
    <div
      id={id}
      aria-live="polite"
      className={cn(
        'flex h-8 items-center rounded-md border border-dashed border-input bg-muted/30 px-3 text-sm',
        region ? 'font-medium' : 'text-muted-foreground',
      )}
    >
      {region ?? (hasCountry ? 'No region' : 'Set by country')}
    </div>
  );
}

/**
 * Location catalog admin page (Location Labels feature). Administrators manage the list of
 * site labels (e.g. "Seattle HQ", "Chicago Warehouse") that technicians select in Media Builder
 * when preparing USB boot media, and that any signed-in user can pick as their own "my location"
 * filter preference from the Header account menu. A location's country decides its region (see
 * the Regions tab); both feed Autopilot group tag templates.
 */
export default function LocationsPage(): React.ReactElement {
  const { notify } = useToast();
  const { refreshLocations } = useUserPreferences();
  const [activeTab, setActiveTab] = useState<Tab>('locations');
  const [locations, setLocations] = useState<LocationEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [regionsView, setRegionsView] = useState<RegionsView | null>(null);
  const [regionsError, setRegionsError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [newLocation, setNewLocation] = useState<LocationDraft>(EMPTY_DRAFT);
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<LocationDraft>(EMPTY_DRAFT);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<LocationEntry | null>(null);
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [wifiNetworks, setWifiNetworks] = useState<WifiNetwork[]>([]);
  const [setupProfiles, setSetupProfiles] = useState<SetupProfileSummary[]>([]);
  const [wifiFor, setWifiFor] = useState<{ location: LocationEntry; afterAdd: boolean } | null>(null);
  const [importing, setImporting] = useState(false);

  const countries = countryOptions().map((c) => ({ value: c.code, label: c.name, description: c.code }));
  const previewRegion = (countryCode: string) =>
    regionsView ? regionCodeFor(regionsView.regions, regionsView.countries, countryCode) : null;

  const loadLocations = async () => {
    setLoading(true);
    try {
      const res = await apiFetchWithRetry('/api/locations', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as LocationEntry[];
        setLocations(data.sort((a, b) => a.name.localeCompare(b.name)));
      } else {
        notify({ status: 'error', title: 'Failed to load locations.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setLoading(false);
    }
  };

  const loadRegions = async () => {
    try {
      const res = await apiFetchWithRetry('/api/regions', { credentials: 'include' });
      if (!res.ok) throw new Error(await extractErrorDetail(res, 'Could not load regions.'));
      setRegionsView(await res.json() as RegionsView);
      setRegionsError(null);
    } catch (err) {
      setRegionsError(err instanceof Error ? err.message : 'Could not reach the server.');
    }
  };

  // Wi-Fi is an add-on to this page: if it fails to load, locations still work without the column.
  const loadWifiNetworks = async (): Promise<WifiNetwork[]> => {
    try {
      const res = await apiFetchWithRetry('/api/wifi-networks', { credentials: 'include' });
      if (!res.ok) return [];
      const data = await res.json() as WifiNetwork[];
      setWifiNetworks(data);
      return data;
    } catch {
      return [];
    }
  };

  useEffect(() => { void loadLocations(); void loadRegions(); void loadWifiNetworks(); }, []);

  // Loaded when a delete is about to be confirmed, so the confirmation can name the profile that
  // loses the location. A failure only drops that sentence; the server still refuses unsafe deletes.
  useEffect(() => {
    if (!deleting) return;
    fetchSetupProfiles().then(setSetupProfiles).catch(() => setSetupProfiles([]));
  }, [deleting]);

  // Shared by the add form and the importer, so both go through the same endpoint and validation.
  const createLocation = async (name: string, countryCode: string | null): Promise<LocationEntry | null> => {
    try {
      const res = await apiFetch('/api/locations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ name: name.trim(), countryCode: countryCode || null }),
      });
      return res.ok || res.status === 201 ? await res.json() as LocationEntry : null;
    } catch {
      return null;
    }
  };

  const handleCreate = async () => {
    const trimmed = newLocation.name.trim();
    if (!trimmed || creating) return;
    setCreating(true);
    try {
      const created = await createLocation(trimmed, newLocation.countryCode || null);
      if (created) {
        setNewLocation(EMPTY_DRAFT);
        await loadLocations();
        void loadRegions();
        void refreshLocations();
        notify({ status: 'success', title: `Location "${trimmed}" added.` });
        // Only worth asking when some network is limited to chosen locations; "all locations" ones already apply.
        const networks = await loadWifiNetworks();
        if (networks.some(n => !n.allLocations)) setWifiFor({ location: created, afterAdd: true });
      } else {
        notify({ status: 'error', title: 'Failed to add location.', description: 'The location was not added.' });
      }
    } finally {
      setCreating(false);
    }
  };

  const startEdit = (loc: LocationEntry) => {
    setEditingId(loc.locationId);
    setEditDraft({ name: loc.name, countryCode: loc.countryCode ?? '' });
  };

  const handleSaveEdit = async () => {
    if (!editingId || !editDraft.name.trim()) return;
    setSaving(true);
    try {
      const res = await apiFetch(`/api/locations/${editingId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(toPayload(editDraft)),
      });
      if (res.ok) {
        setEditingId(null);
        await loadLocations();
        void loadRegions();
        void refreshLocations();
      } else {
        notify({ status: 'error', title: 'Failed to update location.', description: await extractErrorDetail(res, 'The location was not updated.') });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!deleting) return;
    setDeletingBusy(true);
    try {
      const res = await apiFetch(`/api/locations/${deleting.locationId}`, { method: 'DELETE', credentials: 'include' });
      if (res.ok || res.status === 204) {
        setDeleting(null);
        await loadLocations();
        void loadRegions();
        void loadWifiNetworks();
        void refreshLocations();
      } else {
        notify({ status: 'error', title: 'Failed to delete location.', description: await extractErrorDetail(res, 'The location was not deleted.') });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setDeletingBusy(false);
    }
  };

  const term = search.trim().toLowerCase();
  const visibleLocations = term
    ? locations.filter(l => [l.name, l.countryCode ? countryName(l.countryCode) : null, l.countryCode, l.effectiveRegion]
      .some(v => v?.toLowerCase().includes(term)))
    : locations;
  const hasWifi = wifiNetworks.length > 0;
  const columns = hasWifi ? 6 : 5;

  const deleteImpact = (loc: LocationEntry): string => {
    const base = 'Sessions already tagged with it keep showing its name, but it can no longer be selected in Media Builder. Pending Autopilot requests from it lose its region and country for group tag templates.';
    const lost = wifiNetworks.filter(n => n.enabled && !n.allLocations && n.locationIds.includes(loc.locationId)).map(n => n.ssid);
    const profile = setupProfiles.find(p => !p.targeting.allDevices && p.targeting.locationIds.includes(loc.locationId));
    const profileNote = profile ? ` It is also removed from setup profile ${profile.name}.` : '';
    if (lost.length === 0) return `${base}${profileNote}`;
    return `${base} Once they are next online, devices there no longer join the Wi-Fi network${lost.length === 1 ? '' : 's'} ${describeNames(lost, 'networks')} automatically.${profileNote}`;
  };

  return (
    <div className="space-y-6">
      <div className="border-b border-border">
        <nav className="flex flex-wrap gap-1" role="tablist" aria-label="Location areas">
          {TABS.map(t => (
            <button
              key={t.key}
              type="button"
              role="tab"
              aria-selected={activeTab === t.key}
              onClick={() => setActiveTab(t.key)}
              className={cn(
                '-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                activeTab === t.key
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {t.label}
            </button>
          ))}
        </nav>
      </div>

      {activeTab === 'regions' && (
        <RegionsPanel
          view={regionsView}
          loadError={regionsError}
          locations={locations}
          onSaved={async () => { await Promise.all([loadRegions(), loadLocations()]); }}
        />
      )}

      {activeTab === 'locations' && (
      <div className="space-y-4">
      <Card>
        <CardContent className="py-5">
          <div className="grid gap-3 sm:grid-cols-[minmax(0,2fr)_minmax(0,1.5fr)_minmax(0,1fr)_auto] sm:items-end">
            <div className="space-y-2">
              <Label htmlFor="new-location-name">Name</Label>
              <Input
                id="new-location-name"
                placeholder="e.g. Seattle HQ"
                value={newLocation.name}
                onChange={e => setNewLocation({ ...newLocation, name: e.target.value })}
                onKeyDown={e => { if (e.key === 'Enter') void handleCreate(); }}
                maxLength={100}
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="new-location-country">Country (optional)</Label>
              <Select
                id="new-location-country"
                value={newLocation.countryCode}
                onValueChange={v => setNewLocation({ ...newLocation, countryCode: v })}
                options={countries}
                placeholder="No country"
                allowEmpty
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="new-location-region">Region</Label>
              <RegionPreview id="new-location-region" region={previewRegion(newLocation.countryCode)} hasCountry={newLocation.countryCode !== ''} />
            </div>
            <Button onClick={() => void handleCreate()} disabled={!newLocation.name.trim() || creating}>
              <Plus />
              Add location
            </Button>
          </div>
        </CardContent>
      </Card>

      <div className="rounded-md border border-border overflow-hidden">
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
          <div className="flex items-center gap-2">
            <h2 className="text-sm font-semibold">Locations</h2>
            <CountPill count={visibleLocations.length} total={locations.length} />
          </div>
          <div className="flex-1" />
          <SearchInput value={search} onValueChange={setSearch} placeholder="Search locations" aria-label="Search locations" wrapperClassName="w-64" />
          <Button variant="outline" onClick={() => setImporting(true)}>
            <Upload />
            Import
          </Button>
        </div>
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[30%]">Name</TableHead>
              <TableHead className="w-[24%]">Country</TableHead>
              <TableHead className="w-[14%]">Region</TableHead>
              {hasWifi && <TableHead className="w-[12%]">Wi-Fi networks</TableHead>}
              <TableHead className="w-[18%]">Created</TableHead>
              <TableHead className={hasWifi ? 'w-[136px]' : 'w-[96px]'}>Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableSkeletonRows columns={columns} rows={3} />
            ) : locations.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columns} className="p-0">
                  <EmptyState
                    icon={MapPin}
                    title="No locations defined yet"
                    description="Add a location above so technicians can select it in Media Builder when preparing USB boot media."
                  />
                </TableCell>
              </TableRow>
            ) : visibleLocations.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columns} className="p-0">
                  <EmptyState icon={MapPin} title="No locations match" description="Clear the search to see every location." />
                </TableCell>
              </TableRow>
            ) : visibleLocations.map(loc => editingId === loc.locationId ? (
              <TableRow key={loc.locationId}>
                <TableCell>
                  <Input aria-label="Location name" value={editDraft.name} maxLength={100} onChange={e => setEditDraft({ ...editDraft, name: e.target.value })} />
                </TableCell>
                <TableCell>
                  <Select aria-label="Country" value={editDraft.countryCode} onValueChange={v => setEditDraft({ ...editDraft, countryCode: v })}
                    options={countries} placeholder="No country" allowEmpty />
                </TableCell>
                <TableCell>
                  <RegionPreview region={previewRegion(editDraft.countryCode)} hasCountry={editDraft.countryCode !== ''} />
                </TableCell>
                {hasWifi && <TableCell className="tabular-nums">{networksForLocation(wifiNetworks, loc.locationId).length}</TableCell>}
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(loc.createdAt)}</TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content="Save changes">
                      <Button variant="ghost" size="icon" aria-label={`Save location ${loc.name}`}
                        disabled={saving || !editDraft.name.trim()} onClick={() => void handleSaveEdit()}>
                          <Check />
                      </Button>
                    </Tooltip>
                    <Tooltip content="Cancel">
                      <Button variant="ghost" size="icon" aria-label="Cancel editing" disabled={saving} onClick={() => setEditingId(null)}>
                          <X />
                      </Button>
                    </Tooltip>
                  </div>
                </TableCell>
              </TableRow>
            ) : (
              <TableRow key={loc.locationId}>
                <TableCell className="font-medium">{loc.name}</TableCell>
                <TableCell className="text-sm">
                  {loc.countryCode ? `${countryName(loc.countryCode) ?? loc.countryCode} (${loc.countryCode})` : <span className="text-muted-foreground">-</span>}
                </TableCell>
                <TableCell className="text-sm">
                  {!loc.effectiveRegion ? (
                    <span className="text-muted-foreground">-</span>
                  ) : loc.regionIsLegacy ? (
                    <Tooltip content="Typed before regions came from countries. Edit the location to use its country's region.">
                      <Badge variant="muted">{loc.effectiveRegion}</Badge>
                    </Tooltip>
                  ) : loc.effectiveRegion}
                </TableCell>
                {hasWifi && (
                  <TableCell className="tabular-nums">
                    {networksForLocation(wifiNetworks, loc.locationId).length || <span className="text-muted-foreground">-</span>}
                  </TableCell>
                )}
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(loc.createdAt)}</TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content="Edit this location">
                      <Button variant="ghost" size="icon" aria-label={`Edit location ${loc.name}`} onClick={() => startEdit(loc)}>
                          <Pencil />
                      </Button>
                    </Tooltip>
                    {hasWifi && (
                      <Tooltip content="Choose Wi-Fi networks">
                        <Button variant="ghost" size="icon" aria-label={`Choose Wi-Fi networks for ${loc.name}`} onClick={() => setWifiFor({ location: loc, afterAdd: false })}>
                          <Wifi />
                        </Button>
                      </Tooltip>
                    )}
                    <Tooltip content="Delete this location">
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={`Delete location ${loc.name}`}
                        className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                        onClick={() => setDeleting(loc)}
                      >
                       <Trash2 />
                      </Button>
                    </Tooltip>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
      </div>
      )}

      {importing && (
        <LocationImportDialog
          existingNames={locations.map(l => l.name)}
          regionsView={regionsView}
          onCreate={async (name, countryCode) => await createLocation(name, countryCode) !== null}
          onClose={() => setImporting(false)}
          onImported={() => {
            void loadLocations();
            void loadRegions();
            void refreshLocations();
          }}
        />
      )}

      {deleting && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `Delete location "${deleting.name}"?`,
            impact: deleteImpact(deleting),
            confirmLabel: 'Delete',
            destructive: true,
          }}
          busy={deletingBusy}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void handleDelete()}
          titleId="delete-location-title"
        />
      )}

      {wifiFor && (
        <LocationWifiDialog
          location={wifiFor.location}
          networks={wifiNetworks}
          afterAdd={wifiFor.afterAdd}
          onClose={() => setWifiFor(null)}
          onSaved={() => {
            notify({ status: 'success', title: `Wi-Fi networks for "${wifiFor.location.name}" saved.`, description: 'Devices there pick them up the next time they are online.' });
            setWifiFor(null);
            void loadWifiNetworks();
          }}
        />
      )}
    </div>
  );
}
