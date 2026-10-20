import { useEffect, useRef, useState } from 'react';
import { ArrowDown, ArrowUp, Info, Lock, Pencil, Plus, Trash2, Wifi } from 'lucide-react';
import { Badge } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { RelativeTime } from '../components/ui/relative-time.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import { WifiNetworkDialog, type PickerLocation } from '../components/WifiNetworkDialog.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { describeNames } from '../lib/regions.ts';
import { MAX_WIFI_NETWORKS, SECURITY_LABELS, type WifiNetwork } from '../lib/wifiNetworks.ts';

type DialogState = { mode: 'add' } | { mode: 'edit'; network: WifiNetwork } | null;

function InfoPopover(): React.ReactElement {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const onEscape = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onEscape);
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onEscape);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <Button variant="ghost" size="icon" aria-label="How Wi-Fi networks work" aria-expanded={open} aria-controls="wifi-info-popover" onClick={() => setOpen((o) => !o)}>
        <Info />
      </Button>
      {open && (
        <div id="wifi-info-popover" className="absolute right-0 top-full z-20 mt-2 w-96 animate-popover-in rounded-xl border border-border bg-popover p-4 text-popover-foreground shadow-xl">
          <p className="text-sm font-semibold">How Wi-Fi networks work</p>
          <ul className="mt-2 list-disc space-y-2 pl-4 text-sm text-muted-foreground">
            <li>Devices that start from a Wi-Fi boot image join these networks automatically when Ethernet has no IP address, in the order listed.</li>
            <li>Passwords are stored in Key Vault. You can replace a password, but never view it.</li>
            <li>Media Builder copies each location&apos;s networks to its USB media, encrypted with the boot media certificate, so devices can get online before reaching the service.</li>
            <li>Anyone holding prepared USB media can recover these passwords. Use a dedicated imaging network, not your corporate one.</li>
          </ul>
        </div>
      )}
    </div>
  );
}

function LocationsCell({ network, locations }: { network: WifiNetwork; locations: PickerLocation[] }): React.ReactElement {
  if (network.allLocations) return <Badge variant="outline">All locations</Badge>;
  const names = network.locationIds
    .map((id) => locations.find((l) => l.locationId === id)?.name)
    .filter((n): n is string => Boolean(n))
    .sort((a, b) => a.localeCompare(b));
  if (names.length === 0) return <span className="text-sm text-muted-foreground">No locations</span>;
  const rest = names.slice(1);
  return (
    <div className="flex flex-wrap items-center gap-1">
      <Badge variant="outline">{names[0]}</Badge>
      {rest.length > 0 && (
        <Tooltip content={`${describeNames(rest.slice(0, 5))}${rest.length > 5 ? ` and ${String(rest.length - 5)} more` : ''}. Edit the network to see and search all ${String(names.length)}.`}>
          <Badge variant="muted" tabIndex={0} className="focus-visible:ring-2 focus-visible:ring-ring">+{rest.length} more</Badge>
        </Tooltip>
      )}
    </div>
  );
}

/**
 * Wi-Fi networks that devices started from a Windows RE boot image join automatically, tried in
 * the order shown. Administrators add, edit, reorder and delete them; passwords are write-only.
 */
export default function WifiNetworksPage(): React.ReactElement {
  const { notify } = useToast();
  const [networks, setNetworks] = useState<WifiNetwork[]>([]);
  const [locations, setLocations] = useState<PickerLocation[]>([]);
  const [loading, setLoading] = useState(true);
  const [dialog, setDialog] = useState<DialogState>(null);
  const [deleting, setDeleting] = useState<WifiNetwork | null>(null);
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [reordering, setReordering] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      const [networksRes, locationsRes] = await Promise.all([
        apiFetchWithRetry('/api/wifi-networks', { credentials: 'include' }),
        apiFetchWithRetry('/api/locations', { credentials: 'include' }),
      ]);
      if (!networksRes.ok) throw new Error(await extractErrorDetail(networksRes, 'Could not load Wi-Fi networks.'));
      setNetworks((await networksRes.json() as WifiNetwork[]).sort((a, b) => a.order - b.order));
      if (locationsRes.ok) setLocations((await locationsRes.json() as PickerLocation[]).sort((a, b) => a.name.localeCompare(b.name)));
    } catch (err) {
      notify({ status: 'error', title: 'Failed to load Wi-Fi networks.', description: err instanceof Error ? err.message : undefined });
    } finally {
      setLoading(false);
    }
  };

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { void load(); }, []);

  const move = async (index: number, delta: -1 | 1) => {
    const next = [...networks];
    const [item] = next.splice(index, 1);
    next.splice(index + delta, 0, item);
    setNetworks(next.map((n, i) => ({ ...n, order: i + 1 })));
    setReordering(true);
    try {
      const res = await apiFetch('/api/wifi-networks/order', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ networkIds: next.map((n) => n.networkId) }),
      });
      if (!res.ok) throw new Error(await extractErrorDetail(res, 'The order was not saved.'));
    } catch (err) {
      notify({ status: 'error', title: 'Order not saved.', description: err instanceof Error ? err.message : undefined });
      await load();
    } finally {
      setReordering(false);
    }
  };

  const handleDelete = async () => {
    if (!deleting) return;
    setDeletingBusy(true);
    try {
      const res = await apiFetch(`/api/wifi-networks/${deleting.networkId}`, { method: 'DELETE', credentials: 'include' });
      if (!res.ok && res.status !== 204) throw new Error(await extractErrorDetail(res, 'The network was not deleted.'));
      notify({ status: 'success', title: `Network "${deleting.ssid}" deleted.` });
      setDeleting(null);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Failed to delete the network.', description: err instanceof Error ? err.message : undefined });
    } finally {
      setDeletingBusy(false);
    }
  };

  const deleteImpact = (network: WifiNetwork): string => {
    const where = network.allLocations
      ? 'every location'
      : describeNames(network.locationIds.map((id) => locations.find((l) => l.locationId === id)?.name).filter((n): n is string => Boolean(n)));
    return `Devices at ${where || 'its locations'} stop receiving ${network.ssid} the next time they are online. Until then, they can still join it from the copy on their USB media.`;
  };

  const atLimit = networks.length >= MAX_WIFI_NETWORKS;
  const addButton = (
    <Tooltip content={atLimit ? `At most ${String(MAX_WIFI_NETWORKS)} networks can be saved.` : 'Add a Wi-Fi network'}>
      <Button onClick={() => setDialog({ mode: 'add' })} disabled={atLimit}><Plus />Add network</Button>
    </Tooltip>
  );

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-muted-foreground">Networks that devices join automatically when they start from a Wi-Fi boot image, tried in the order below.</p>
        <div className="flex shrink-0 items-center gap-2">
          <InfoPopover />
          {addButton}
        </div>
      </div>

      <div className="rounded-md border border-border overflow-hidden">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[72px]">Order</TableHead>
              <TableHead>Network</TableHead>
              <TableHead className="w-[17%]">Security</TableHead>
              <TableHead className="w-[20%]">Locations</TableHead>
              <TableHead className="w-[15%]">Password changed</TableHead>
              <TableHead className="w-[11%]">Status</TableHead>
              <TableHead className="w-[160px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableSkeletonRows columns={7} rows={3} />
            ) : networks.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={7} className="p-0">
                  <EmptyState
                    icon={Wifi}
                    title="No Wi-Fi networks"
                    description="Add a network so devices started from a Windows RE boot image can get online without Ethernet."
                    action={<Button onClick={() => setDialog({ mode: 'add' })}><Plus />Add network</Button>}
                  />
                </TableCell>
              </TableRow>
            ) : networks.map((n, i) => (
              <TableRow key={n.networkId}>
                <TableCell className="tabular-nums">{n.order}</TableCell>
                <TableCell>
                  <p className="truncate font-medium">{n.ssid}</p>
                  {n.hidden && <p className="text-xs text-muted-foreground">Hidden network</p>}
                </TableCell>
                <TableCell>{SECURITY_LABELS[n.security]}</TableCell>
                <TableCell><LocationsCell network={n} locations={locations} /></TableCell>
                <TableCell>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">
                    <Lock className="h-3 w-3" aria-hidden="true" />
                    <RelativeTime value={n.passwordChangedAt} />
                  </span>
                </TableCell>
                <TableCell>
                  <Badge variant={n.enabled ? 'success' : 'muted'} dot>{n.enabled ? 'Enabled' : 'Disabled'}</Badge>
                </TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content="Move up">
                      <Button variant="ghost" size="icon" aria-label={`Move ${n.ssid} up`} className="text-muted-foreground" disabled={i === 0 || reordering} onClick={() => void move(i, -1)}>
                        <ArrowUp />
                      </Button>
                    </Tooltip>
                    <Tooltip content="Move down">
                      <Button variant="ghost" size="icon" aria-label={`Move ${n.ssid} down`} className="text-muted-foreground" disabled={i === networks.length - 1 || reordering} onClick={() => void move(i, 1)}>
                        <ArrowDown />
                      </Button>
                    </Tooltip>
                    <Tooltip content="Edit this network">
                      <Button variant="ghost" size="icon" aria-label={`Edit network ${n.ssid}`} onClick={() => setDialog({ mode: 'edit', network: n })}>
                        <Pencil />
                      </Button>
                    </Tooltip>
                    <Tooltip content="Delete this network">
                      <Button variant="ghost" size="icon" aria-label={`Delete network ${n.ssid}`} className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" onClick={() => setDeleting(n)}>
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

      {dialog && (
        <WifiNetworkDialog
          network={dialog.mode === 'edit' ? dialog.network : undefined}
          networks={networks}
          locations={locations}
          onClose={() => setDialog(null)}
          onSaved={(saved) => {
            setDialog(null);
            notify({ status: 'success', title: `Network "${saved.ssid}" saved.`, description: 'Devices pick it up the next time they are online.' });
            void load();
          }}
        />
      )}

      {deleting && (
        <ConfirmImpactDialog
          copy={{ confirmTitle: `Delete Wi-Fi network "${deleting.ssid}"?`, impact: deleteImpact(deleting), confirmLabel: 'Delete', destructive: true }}
          busy={deletingBusy}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void handleDelete()}
          titleId="delete-wifi-title"
        />
      )}
    </div>
  );
}
