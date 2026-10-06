import { useState, useEffect } from 'react';
import { Trash2, MapPin, Plus, Pencil, Check, X } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Select } from '../components/ui/select.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import { useToast } from '../context/toastContext.tsx';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { countryName, countryOptions } from '../lib/countries.ts';
import { formatDateTime } from '../lib/utils.ts';

interface LocationEntry {
  locationId: string;
  name: string;
  region?: string | null;
  countryCode?: string | null;
  createdAt: string;
}

interface LocationDraft {
  name: string;
  region: string;
  countryCode: string;
}

const EMPTY_DRAFT: LocationDraft = { name: '', region: '', countryCode: '' };
const REGION_PATTERN = /^[A-Za-z0-9_-]{0,16}$/;

function toPayload(draft: LocationDraft): Record<string, string | null> {
  return {
    name: draft.name.trim(),
    region: draft.region.trim().toUpperCase() || null,
    countryCode: draft.countryCode || null,
  };
}

/**
 * Location catalog admin page (Location Labels feature). Administrators manage the list of
 * site labels (e.g. "Seattle HQ", "Chicago Warehouse") that technicians select in Media Builder
 * when preparing USB boot media, and that any signed-in user can pick as their own "my location"
 * filter preference from the Header account menu. Region and country feed Autopilot group tag
 * templates.
 */
export default function LocationsPage(): React.ReactElement {
  const { notify } = useToast();
  const { refreshLocations } = useUserPreferences();
  const [locations, setLocations] = useState<LocationEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [newLocation, setNewLocation] = useState<LocationDraft>(EMPTY_DRAFT);
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<LocationDraft>(EMPTY_DRAFT);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<LocationEntry | null>(null);
  const [deletingBusy, setDeletingBusy] = useState(false);

  const countries = countryOptions().map((c) => ({ value: c.code, label: c.name, description: c.code }));

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

  useEffect(() => { void loadLocations(); }, []);

  const handleCreate = async () => {
    const trimmed = newLocation.name.trim();
    if (!trimmed || creating || !REGION_PATTERN.test(newLocation.region.trim())) return;
    setCreating(true);
    try {
      const res = await apiFetch('/api/locations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(toPayload(newLocation)),
      });
      if (res.ok || res.status === 201) {
        setNewLocation(EMPTY_DRAFT);
        await loadLocations();
        void refreshLocations();
        notify({ status: 'success', title: `Location "${trimmed}" added.` });
      } else {
        notify({ status: 'error', title: 'Failed to add location.', description: await extractErrorDetail(res, 'The location was not added.') });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setCreating(false);
    }
  };

  const startEdit = (loc: LocationEntry) => {
    setEditingId(loc.locationId);
    setEditDraft({ name: loc.name, region: loc.region ?? '', countryCode: loc.countryCode ?? '' });
  };

  const handleSaveEdit = async () => {
    if (!editingId || !editDraft.name.trim() || !REGION_PATTERN.test(editDraft.region.trim())) return;
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
        void refreshLocations();
      } else {
        notify({ status: 'error', title: 'Failed to delete location.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setDeletingBusy(false);
    }
  };

  const newRegionInvalid = !REGION_PATTERN.test(newLocation.region.trim());
  const editRegionInvalid = !REGION_PATTERN.test(editDraft.region.trim());

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="flex flex-col gap-4 py-5 lg:flex-row lg:items-end">
          <div className="grid flex-1 gap-3 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1.5fr)_auto] sm:items-end">
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
              <Label htmlFor="new-location-region">Region (optional)</Label>
              <Input
                id="new-location-region"
                placeholder="e.g. EMEA"
                value={newLocation.region}
                onChange={e => setNewLocation({ ...newLocation, region: e.target.value })}
                maxLength={16}
                aria-invalid={newRegionInvalid}
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
            <Button onClick={() => void handleCreate()} disabled={!newLocation.name.trim() || creating || newRegionInvalid}>
              <Plus />
              Add location
            </Button>
          </div>
          <div className="flex items-center gap-3 lg:justify-end">
            <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <MapPin className="h-5 w-5" aria-hidden="true" />
            </div>
            <div>
              <p className="text-sm text-muted-foreground">Location labels</p>
              <p className="text-2xl font-semibold tabular-nums">{locations.length}</p>
            </div>
          </div>
        </CardContent>
        {newRegionInvalid && (
          <p className="px-6 pb-4 text-sm text-destructive">Region may only contain letters, digits, hyphens and underscores.</p>
        )}
      </Card>

      <div className="rounded-md border border-border overflow-hidden">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[34%]">Name</TableHead>
              <TableHead className="w-[14%]">Region</TableHead>
              <TableHead className="w-[22%]">Country</TableHead>
              <TableHead className="w-[18%]">Created</TableHead>
              <TableHead className="w-[96px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableSkeletonRows columns={5} rows={3} />
            ) : locations.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={5} className="p-0">
                  <EmptyState
                    icon={MapPin}
                    title="No locations defined yet"
                    description="Add a location above so technicians can select it in Media Builder when preparing USB boot media."
                  />
                </TableCell>
              </TableRow>
            ) : locations.map(loc => editingId === loc.locationId ? (
              <TableRow key={loc.locationId}>
                <TableCell>
                  <Input aria-label="Location name" value={editDraft.name} maxLength={100} onChange={e => setEditDraft({ ...editDraft, name: e.target.value })} />
                </TableCell>
                <TableCell>
                  <Input aria-label="Region" value={editDraft.region} maxLength={16} aria-invalid={editRegionInvalid}
                    onChange={e => setEditDraft({ ...editDraft, region: e.target.value })} />
                </TableCell>
                <TableCell>
                  <Select aria-label="Country" value={editDraft.countryCode} onValueChange={v => setEditDraft({ ...editDraft, countryCode: v })}
                    options={countries} placeholder="No country" allowEmpty />
                </TableCell>
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(loc.createdAt)}</TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content="Save changes">
                      <Button variant="ghost" size="icon" aria-label={`Save location ${loc.name}`}
                        disabled={saving || !editDraft.name.trim() || editRegionInvalid} onClick={() => void handleSaveEdit()}>
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
                <TableCell className="text-sm">{loc.region ?? <span className="text-muted-foreground">-</span>}</TableCell>
                <TableCell className="text-sm">
                  {loc.countryCode ? `${countryName(loc.countryCode) ?? loc.countryCode} (${loc.countryCode})` : <span className="text-muted-foreground">-</span>}
                </TableCell>
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(loc.createdAt)}</TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content="Edit this location">
                      <Button variant="ghost" size="icon" aria-label={`Edit location ${loc.name}`} onClick={() => startEdit(loc)}>
                          <Pencil />
                      </Button>
                    </Tooltip>
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

      {deleting && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `Delete location "${deleting.name}"?`,
            impact: 'Sessions already tagged with it keep showing its name, but it can no longer be selected in Media Builder. Pending Autopilot requests from it lose its region and country for group tag templates.',
            confirmLabel: 'Delete',
            destructive: true,
          }}
          busy={deletingBusy}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void handleDelete()}
          titleId="delete-location-title"
        />
      )}
    </div>
  );
}
