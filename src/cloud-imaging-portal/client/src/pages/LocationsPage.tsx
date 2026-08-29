import { useState, useEffect } from 'react';
import { Trash2, MapPin, Plus } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { useToast } from '../context/toastContext.tsx';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';

interface LocationEntry {
  locationId: string;
  name: string;
  createdAt: string;
}

/**
 * Location catalog admin page (Location Labels feature). Administrators manage the list of
 * site labels (e.g. "Seattle HQ", "Chicago Warehouse") that technicians select in Media Builder
 * when preparing USB boot media, and that any signed-in user can pick as their own "my location"
 * filter preference from the Header account menu.
 */
export default function LocationsPage(): React.ReactElement {
  const { notify } = useToast();
  const { refreshLocations } = useUserPreferences();
  const [locations, setLocations] = useState<LocationEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [newName, setNewName] = useState('');
  const [creating, setCreating] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

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
    const trimmed = newName.trim();
    if (!trimmed || creating) return;
    setCreating(true);
    try {
      const res = await apiFetch('/api/locations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ name: trimmed }),
      });
      if (res.ok || res.status === 201) {
        setNewName('');
        await loadLocations();
        void refreshLocations();
        notify({ status: 'success', title: `Location "${trimmed}" added.` });
      } else {
        notify({ status: 'error', title: 'Failed to add location.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setCreating(false);
    }
  };

  const handleDelete = async (id: string, name: string) => {
    if (!confirm(`Delete location "${name}"? Sessions already tagged with it keep showing its name, but it will no longer be selectable.`)) return;
    setDeletingId(id);
    try {
      const res = await apiFetch(`/api/locations/${id}`, { method: 'DELETE', credentials: 'include' });
      if (res.ok || res.status === 204) {
        await loadLocations();
        void refreshLocations();
      } else {
        notify({ status: 'error', title: 'Failed to delete location.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setDeletingId(null);
    }
  };

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="flex flex-col gap-3 py-5 sm:flex-row sm:items-center">
          <div className="flex flex-1 items-center gap-2">
            <Input
              placeholder="e.g. Seattle HQ"
              value={newName}
              onChange={e => setNewName(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') void handleCreate(); }}
              maxLength={100}
              className="max-w-sm"
            />
            <Button onClick={() => void handleCreate()} disabled={!newName.trim() || creating}>
              <Plus className="h-4 w-4" />
              Add location
            </Button>
          </div>
          <div className="flex items-center gap-3 sm:justify-end">
            <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <MapPin className="h-5 w-5" aria-hidden="true" />
            </div>
            <div>
              <p className="text-sm text-muted-foreground">Location labels</p>
              <p className="text-2xl font-semibold tabular-nums">{locations.length}</p>
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="rounded-md border border-border overflow-hidden">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[60%]">Name</TableHead>
              <TableHead className="w-[24%]">Created</TableHead>
              <TableHead className="w-[80px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {!loading && locations.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={3} className="p-0">
                  <EmptyState
                    icon={MapPin}
                    title="No locations defined yet"
                    description="Add a location above so technicians can select it in Media Builder when preparing USB boot media."
                  />
                </TableCell>
              </TableRow>
            ) : locations.map(loc => (
              <TableRow key={loc.locationId}>
                <TableCell className="font-medium">{loc.name}</TableCell>
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(loc.createdAt)}</TableCell>
                <TableCell>
                  <Button
                    variant="ghost"
                    size="icon"
                    title="Delete this location"
                    disabled={deletingId === loc.locationId}
                    className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                    onClick={() => void handleDelete(loc.locationId, loc.name)}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
