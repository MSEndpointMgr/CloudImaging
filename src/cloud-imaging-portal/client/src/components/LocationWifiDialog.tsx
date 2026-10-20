import { useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { Plus, Save, X } from 'lucide-react';
import { Badge } from './ui/badge.tsx';
import { Button } from './ui/button.tsx';
import { Checkbox } from './ui/checkbox.tsx';
import { SearchInput } from './ui/search-input.tsx';
import { WifiWarning, type PickerLocation } from './WifiNetworkDialog.tsx';
import { apiFetch, extractErrorDetail } from '../lib/apiClient.ts';
import { describeNames } from '../lib/regions.ts';
import { SECURITY_LABELS, type WifiNetwork } from '../lib/wifiNetworks.ts';

/** Above this many networks the list gets a search field. */
const SEARCH_THRESHOLD = 8;

/**
 * Chooses which saved networks devices at one location join. Only each network's location list
 * changes, so network settings, and with them prepared USB media, are never affected.
 * "All locations" networks show ticked and locked; disabled ones can still be chosen so the
 * location is ready when the network is turned back on.
 */
export function LocationWifiDialog({ location, networks, afterAdd = false, onClose, onSaved }: {
  location: PickerLocation;
  networks: WifiNetwork[];
  /** Shown right after the location was created: dismiss reads "Skip" and nothing is preselected. */
  afterAdd?: boolean;
  onClose: () => void;
  onSaved: () => void;
}): React.ReactElement {
  const ordered = useMemo(() => [...networks].sort((a, b) => a.order - b.order), [networks]);
  const initial = useMemo(
    () => ordered.filter((n) => !n.allLocations && n.locationIds.includes(location.locationId)).map((n) => n.networkId),
    [ordered, location.locationId],
  );
  const [selected, setSelected] = useState<string[]>(initial);
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const term = search.trim().toLowerCase();
  const shown = term ? ordered.filter((n) => n.ssid.toLowerCase().includes(term)) : ordered;
  const removed = ordered.filter((n) => n.enabled && initial.includes(n.networkId) && !selected.includes(n.networkId));
  const added = selected.filter((id) => !initial.includes(id)).length;
  const toggle = (id: string, on: boolean) => setSelected((s) => (on ? [...s, id] : s.filter((x) => x !== id)));

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const res = await apiFetch(`/api/locations/${location.locationId}/wifi-networks`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ networkIds: selected }),
      });
      if (!res.ok) {
        setError(await extractErrorDetail(res, 'The Wi-Fi networks were not saved.'));
        return;
      }
      onSaved();
    } catch {
      setError('Could not reach the server.');
    } finally {
      setBusy(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="location-wifi-title">
      <div className="flex max-h-full w-full max-w-lg flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="location-wifi-title" className="text-base font-semibold break-words">
              {afterAdd ? `Add ${location.name} to Wi-Fi networks?` : `Wi-Fi networks for ${location.name}`}
            </h2>
            <p className="text-sm text-muted-foreground">
              {afterAdd
                ? `Location "${location.name}" added. Choose which saved networks its devices join automatically.`
                : `Choose which saved networks devices at ${location.name} join automatically.`}
            </p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close" disabled={busy}><X /></Button>
        </div>

        <div className="space-y-4 overflow-y-auto p-5">
          {ordered.length > SEARCH_THRESHOLD && (
            <SearchInput value={search} onValueChange={setSearch} placeholder="Search networks" aria-label="Search networks" />
          )}
          <ul className="divide-y divide-border rounded-md border border-border" aria-label="Wi-Fi networks">
            {shown.length === 0 && <li className="px-3 py-2 text-sm text-muted-foreground">No networks match.</li>}
            {shown.map((n) => {
              const labelId = `location-wifi-${n.networkId}`;
              return (
                <li key={n.networkId} className="flex items-center gap-3 px-3 py-2">
                  <Checkbox
                    checked={n.allLocations || selected.includes(n.networkId)}
                    disabled={n.allLocations || busy}
                    onCheckedChange={(on) => toggle(n.networkId, on)}
                    labelledBy={labelId}
                  />
                  <div className="min-w-0 flex-1">
                    <p id={labelId} className="truncate text-sm font-medium">{n.ssid}</p>
                    <p className="text-sm text-muted-foreground">
                      {SECURITY_LABELS[n.security]}{n.hidden ? ', hidden' : ''}{n.allLocations ? '. Sent to all locations.' : ''}
                    </p>
                  </div>
                  {!n.enabled && <Badge variant="muted" dot>Disabled</Badge>}
                </li>
              );
            })}
          </ul>

          {removed.length > 0 && (
            <WifiWarning title={`Devices at ${location.name} will stop joining ${removed.length === 1 ? removed[0].ssid : `${String(removed.length)} networks`} automatically`}>
              Once they are next online, devices at {location.name} no longer join {describeNames(removed.map((n) => n.ssid), 'networks')} automatically. Make sure they have Ethernet or another saved Wi-Fi network.
            </WifiWarning>
          )}
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose} disabled={busy}>{afterAdd ? 'Skip' : 'Cancel'}</Button>
          {afterAdd ? (
            <Button onClick={() => void save()} loading={busy} disabled={busy || added === 0}>
              <Plus />Add to {added} network{added === 1 ? '' : 's'}
            </Button>
          ) : (
            <Button onClick={() => void save()} loading={busy}><Save />Save</Button>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
