import { useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, Eye, EyeOff, FileUp, KeyRound, ListChecks, ListX, Lock, Save, X } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { Checkbox } from './ui/checkbox.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { SearchInput } from './ui/search-input.tsx';
import { Select } from './ui/select.tsx';
import { Switch } from './ui/switch.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { apiFetch, extractErrorDetail } from '../lib/apiClient.ts';
import { describeNames } from '../lib/regions.ts';
import { formatDateTime } from '../lib/utils.ts';
import {
  SECURITY_OPTIONS,
  draftFrom,
  emptyDraft,
  networkSettingsChanged,
  parseWlanProfile,
  removedLocationIds,
  toPayload,
  validatePassword,
  validateSsid,
  type WifiNetwork,
  type WifiNetworkDraft,
  type WifiSecurity,
} from '../lib/wifiNetworks.ts';

/** A location as the picker shows it. */
export interface PickerLocation {
  locationId: string;
  name: string;
  effectiveRegion?: string | null;
  countryCode?: string | null;
}

const SCOPE_OPTIONS = [
  { value: 'all', label: 'All locations', description: 'Also covers locations added later' },
  { value: 'selected', label: 'Selected locations' },
];

/** Amber callout used for every "this change affects devices" warning in the Wi-Fi dialogs. */
export function WifiWarning({ title, children }: { title: string; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="flex items-start gap-3 rounded-md border border-amber-500/40 bg-amber-500/10 p-4">
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700 dark:text-amber-400" aria-hidden="true" />
      <div className="space-y-1">
        <p className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{children}</p>
      </div>
    </div>
  );
}

/** Names the locations that lose a network: "All 140 locations", or the first three and a count. */
function removalTitleAndNames(removedIds: string[], locations: PickerLocation[]): { title: string; names: string } {
  const names = removedIds.map((id) => locations.find((l) => l.locationId === id)?.name ?? 'a deleted location');
  const count = removedIds.length;
  const title = `${count === locations.length && count > 3 ? `All ${String(count)}` : String(count)} location${count === 1 ? '' : 's'} will stop connecting automatically`;
  return { title, names: describeNames(names) };
}

function LocationPicker({ locations, selected, onChange }: {
  locations: PickerLocation[];
  selected: string[];
  onChange: (ids: string[]) => void;
}): React.ReactElement {
  const [search, setSearch] = useState('');
  const term = search.trim().toLowerCase();
  const shown = useMemo(
    () => (term ? locations.filter((l) => [l.name, l.effectiveRegion, l.countryCode].some((v) => v?.toLowerCase().includes(term))) : locations),
    [locations, term],
  );
  const selectedSet = new Set(selected);
  const scope = term ? `the ${String(shown.length)} location${shown.length === 1 ? '' : 's'} shown` : 'every location';

  const toggle = (id: string, on: boolean) => onChange(on ? [...selected, id] : selected.filter((s) => s !== id));
  const selectShown = () => onChange([...new Set([...selected, ...shown.map((l) => l.locationId)])]);
  const clearShown = () => {
    const hide = new Set(shown.map((l) => l.locationId));
    onChange(selected.filter((id) => !hide.has(id)));
  };

  return (
    <div className="rounded-md border border-border">
      <div className="flex flex-wrap items-center gap-2 border-b border-border p-2">
        <SearchInput value={search} onValueChange={setSearch} placeholder="Search locations" aria-label="Search locations" wrapperClassName="min-w-40 flex-1" />
        <Tooltip content={`Select ${scope}`}>
          <Button variant="outline" onClick={selectShown} disabled={shown.length === 0}><ListChecks />Select all</Button>
        </Tooltip>
        <Tooltip content={`Clear ${scope}`}>
          <Button variant="outline" onClick={clearShown} disabled={shown.length === 0}><ListX />Clear</Button>
        </Tooltip>
      </div>
      <ul className="h-56 overflow-y-auto py-1" aria-label="Locations">
        {shown.length === 0 && <li className="px-3 py-2 text-sm text-muted-foreground">{locations.length === 0 ? 'No locations exist yet.' : 'No locations match.'}</li>}
        {shown.map((l) => {
          const labelId = `wifi-loc-${l.locationId}`;
          return (
            <li key={l.locationId} className="flex items-center gap-3 px-3 py-1">
              <Checkbox checked={selectedSet.has(l.locationId)} onCheckedChange={(on) => toggle(l.locationId, on)} labelledBy={labelId} />
              <span id={labelId} className="min-w-0 flex-1 truncate text-sm">{l.name}</span>
              <span className="shrink-0 text-xs text-muted-foreground">{[l.effectiveRegion, l.countryCode].filter(Boolean).join(', ')}</span>
            </li>
          );
        })}
      </ul>
      <div className="flex justify-between border-t border-border px-3 py-2 text-sm text-muted-foreground">
        <span>{selected.length} of {locations.length} selected</span>
        {term && <span>{shown.length} shown</span>}
      </div>
    </div>
  );
}

function SettingRow({ title, help, children, labelId }: { title: string; help: string; children: React.ReactNode; labelId?: string }): React.ReactElement {
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="space-y-1">
        <p id={labelId} className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{help}</p>
      </div>
      {children}
    </div>
  );
}

/**
 * Add or edit a saved Wi-Fi network. On edit, warnings compare the form with the saved network:
 * removing locations takes connectivity away, and changing network settings makes prepared USB
 * media out of date.
 */
export function WifiNetworkDialog({ network, networks, locations, onClose, onSaved }: {
  /** The network to edit; omit to add one. */
  network?: WifiNetwork;
  networks: WifiNetwork[];
  locations: PickerLocation[];
  onClose: () => void;
  onSaved: (saved: WifiNetwork) => void;
}): React.ReactElement {
  const editing = network !== undefined;
  const [draft, setDraft] = useState<WifiNetworkDraft>(() => (network ? draftFrom(network) : emptyDraft()));
  const [replacingPassword, setReplacingPassword] = useState(!editing);
  const [showPassword, setShowPassword] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const [importError, setImportError] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const set = (patch: Partial<WifiNetworkDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const ssidError = validateSsid(draft.ssid, networks, network?.networkId);
  const passwordError = replacingPassword ? validatePassword(draft.password) : null;
  const locationsError = !draft.allLocations && draft.locationIds.length === 0 ? 'Choose at least one location, or All locations.' : null;
  const invalid = Boolean(ssidError ?? passwordError ?? locationsError);

  const allIds = locations.map((l) => l.locationId);
  const removed = network ? removedLocationIds(network, draft, allIds) : [];
  const removal = removed.length > 0 ? removalTitleAndNames(removed, locations) : null;
  const staleMedia = network ? networkSettingsChanged(network, { ...draft, password: replacingPassword ? draft.password : '' }) : false;

  const onImport = async (file: File | undefined) => {
    if (!file) return;
    const parsed = parseWlanProfile(await file.text());
    if ('error' in parsed) {
      setImportError(parsed.error);
      return;
    }
    setImportError(null);
    set({ ssid: parsed.ssid, hidden: parsed.hidden, security: parsed.security, password: parsed.password });
  };

  const save = async () => {
    setAttempted(true);
    if (invalid) return;
    setBusy(true);
    setServerError(null);
    try {
      const payload = toPayload({ ...draft, password: replacingPassword ? draft.password : '' });
      const res = await apiFetch(editing ? `/api/wifi-networks/${network.networkId}` : '/api/wifi-networks', {
        method: editing ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(payload),
      });
      if (!res.ok) {
        setServerError(await extractErrorDetail(res, 'The network was not saved.'));
        return;
      }
      onSaved(await res.json() as WifiNetwork);
    } catch {
      setServerError('Could not reach the server.');
    } finally {
      setBusy(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="wifi-dialog-title">
      <div className="flex max-h-full w-full max-w-xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="wifi-dialog-title" className="text-base font-semibold break-words">{editing ? `Edit ${network.ssid}` : 'Add Wi-Fi network'}</h2>
            <p className="text-sm text-muted-foreground">
              {editing ? 'Devices pick up changes the next time they are online.' : 'Devices at the selected locations join it automatically.'}
            </p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close" disabled={busy}><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          {!editing && (
            <div className="space-y-2">
              <SettingRow title="Start from a Windows profile" help="Fills in the form from a netsh wlan export profile key=clear file. The file is not kept.">
                <Button variant="outline" onClick={() => fileRef.current?.click()}><FileUp />Import</Button>
              </SettingRow>
              <input ref={fileRef} type="file" accept=".xml,text/xml,application/xml" className="hidden" aria-hidden="true" tabIndex={-1}
                onChange={(e) => { void onImport(e.target.files?.[0]); e.target.value = ''; }} />
              {importError && <p role="alert" className="text-sm text-destructive">{importError}</p>}
            </div>
          )}

          <div className="space-y-2">
            <Label htmlFor="wifi-ssid">Network name (SSID)</Label>
            <Input id="wifi-ssid" value={draft.ssid} maxLength={32} onChange={(e) => set({ ssid: e.target.value })} aria-invalid={attempted && ssidError !== null} />
            {attempted && ssidError && <p className="text-sm text-destructive">{ssidError}</p>}
          </div>

          <SettingRow title="Hidden network" help="Turn on if the network does not broadcast its name.">
            <Switch checked={draft.hidden} onCheckedChange={(hidden) => set({ hidden })} label="Hidden network" />
          </SettingRow>

          <div className="space-y-2">
            <Label htmlFor="wifi-security">Security</Label>
            <p className="text-sm text-muted-foreground">Enterprise (802.1X) networks can&apos;t be used from boot media.</p>
            <Select id="wifi-security" value={draft.security} onValueChange={(v) => set({ security: v as WifiSecurity })} options={SECURITY_OPTIONS} />
          </div>

          <div className="space-y-2">
            <Label htmlFor="wifi-password">Password</Label>
            {replacingPassword ? (
              <>
                <p className="text-sm text-muted-foreground">8 to 63 characters. You can replace it later, but never view it.</p>
                <div className="flex items-center gap-2">
                  <Input id="wifi-password" type={showPassword ? 'text' : 'password'} autoComplete="new-password" value={draft.password}
                    onChange={(e) => set({ password: e.target.value })} aria-invalid={attempted && passwordError !== null} />
                  <Tooltip content={showPassword ? 'Hide password' : 'Show password'}>
                    <Button variant="ghost" size="icon" aria-label={showPassword ? 'Hide password' : 'Show password'} onClick={() => setShowPassword((s) => !s)}>
                      {showPassword ? <EyeOff /> : <Eye />}
                    </Button>
                  </Tooltip>
                </div>
                {attempted && passwordError && <p className="text-sm text-destructive">{passwordError}</p>}
              </>
            ) : (
              <div className="flex items-center justify-between gap-4 rounded-md border border-border p-3">
                <div className="flex items-start gap-3">
                  <Lock className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <div>
                    <p className="text-sm font-medium">Password is set</p>
                    <p className="text-sm text-muted-foreground">
                      Changed {formatDateTime(network!.passwordChangedAt)}{network!.passwordChangedBy ? ` by ${network!.passwordChangedBy}` : ''}
                    </p>
                  </div>
                </div>
                <Button variant="outline" onClick={() => setReplacingPassword(true)}><KeyRound />Replace</Button>
              </div>
            )}
          </div>

          <div className="space-y-2">
            <Label htmlFor="wifi-scope">Locations</Label>
            <p className="text-sm text-muted-foreground">USB media prepared for these locations, and devices registered at them, receive this network.</p>
            <Select id="wifi-scope" value={draft.allLocations ? 'all' : 'selected'} onValueChange={(v) => set({ allLocations: v === 'all' })} options={SCOPE_OPTIONS} />
            {!draft.allLocations && <LocationPicker locations={locations} selected={draft.locationIds} onChange={(locationIds) => set({ locationIds })} />}
            {attempted && locationsError && <p className="text-sm text-destructive">{locationsError}</p>}
          </div>

          <SettingRow title="Enabled" help="Disabled networks stay saved but are not sent to devices.">
            <Switch checked={draft.enabled} onCheckedChange={(enabled) => set({ enabled })} label="Enabled" />
          </SettingRow>

          {removal && (
            <WifiWarning title={removal.title}>
              Once they are next online, devices at {removal.names} no longer join {network!.ssid} automatically. Make sure they have Ethernet or another saved Wi-Fi network.
            </WifiWarning>
          )}
          {staleMedia && (
            <WifiWarning title="Prepared USB media has the old network settings">
              Devices update once they are online. Devices at these locations without Ethernet need their USB media prepared again.
            </WifiWarning>
          )}
          {serverError && <p role="alert" className="text-sm text-destructive">{serverError}</p>}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button onClick={() => void save()} loading={busy}><Save />{editing ? 'Save changes' : 'Save network'}</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
