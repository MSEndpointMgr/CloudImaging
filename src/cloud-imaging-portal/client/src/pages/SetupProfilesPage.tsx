import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Eye, Pencil, Plus, SlidersHorizontal, Trash2 } from 'lucide-react';
import { Button } from '../components/ui/button.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import { TargetingBadges } from '../components/TargetingBadges.tsx';
import type { PickerLocation } from '../components/LocationPicker.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatTimestamp } from '../lib/utils.ts';
import { deleteImpact, deleteSetupProfile, fetchSetupProfiles, targetedNames, type SetupProfileSummary } from '../lib/setupProfiles.ts';

/**
 * Setup profiles: registry values, scripts and an answer file applied to devices before Windows
 * starts and during Windows setup. Technicians can look; Operators and Administrators change them.
 */
export default function SetupProfilesPage(): React.ReactElement {
  const navigate = useNavigate();
  const { notify } = useToast();
  const { canManageSetupProfiles } = useAuth();
  const [profiles, setProfiles] = useState<SetupProfileSummary[]>([]);
  const [locations, setLocations] = useState<PickerLocation[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<SetupProfileSummary | null>(null);
  const [deletingBusy, setDeletingBusy] = useState(false);

  const load = async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const [list, locationsRes] = await Promise.all([
        fetchSetupProfiles(),
        apiFetchWithRetry('/api/locations', { credentials: 'include' }),
      ]);
      setProfiles(list.sort((a, b) => a.name.localeCompare(b.name)));
      if (locationsRes.ok) setLocations((await locationsRes.json() as PickerLocation[]).sort((a, b) => a.name.localeCompare(b.name)));
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load setup profiles.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void load(); }, []);

  const handleDelete = async () => {
    if (!deleting) return;
    setDeletingBusy(true);
    try {
      await deleteSetupProfile(deleting.profileId);
      notify({ status: 'success', title: `Setup profile "${deleting.name}" deleted.` });
      setDeleting(null);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Failed to delete the setup profile.', description: err instanceof Error ? err.message : undefined });
    } finally {
      setDeletingBusy(false);
    }
  };

  const newButton = <Button onClick={() => navigate('/setup-profiles/new')}><Plus />New profile</Button>;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-muted-foreground">
          Registry values and scripts applied to devices before Windows starts, targeted at all devices or at the location set on the USB media they started from.
        </p>
        {canManageSetupProfiles && <div className="shrink-0">{newButton}</div>}
      </div>

      <div className="overflow-hidden rounded-md border border-border">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className="w-[26%]">Name</TableHead>
              <TableHead>Targeting</TableHead>
              <TableHead className="w-[140px] text-right">Registry values</TableHead>
              <TableHead className="w-[90px] text-right">Scripts</TableHead>
              <TableHead className="w-[160px]">Last modified</TableHead>
              <TableHead className="w-[96px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableSkeletonRows columns={6} rows={3} />
            ) : loadError ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="py-8 text-center">
                  <p className="text-sm text-destructive">{loadError}</p>
                  <Button variant="link" onClick={() => void load()}>Try again</Button>
                </TableCell>
              </TableRow>
            ) : profiles.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="p-0">
                  <EmptyState
                    icon={SlidersHorizontal}
                    title="No setup profiles yet"
                    description="Set registry values and run scripts that must be in place before Windows Autopilot starts. Target a profile at locations or at all devices."
                    action={canManageSetupProfiles ? newButton : undefined}
                  />
                </TableCell>
              </TableRow>
            ) : profiles.map((p) => (
              <TableRow key={p.profileId}>
                <TableCell><p className="truncate font-medium">{p.name}</p></TableCell>
                <TableCell>
                  <TargetingBadges all={p.targeting.allDevices} locationIds={p.targeting.locationIds} locations={locations} allLabel="All devices" editNoun="profile" />
                </TableCell>
                <TableCell className="text-right tabular-nums">{p.registryValueCount}</TableCell>
                <TableCell className="text-right tabular-nums">{p.scriptCount}</TableCell>
                <TableCell>
                  <Tooltip content={p.lastModifiedBy ? `By ${p.lastModifiedBy}` : 'Author unknown'}>
                    <span tabIndex={0} className="tabular-nums">{formatTimestamp(p.lastModifiedAt)}</span>
                  </Tooltip>
                </TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Tooltip content={canManageSetupProfiles ? 'Edit' : 'View'}>
                      <Button variant="ghost" size="icon" aria-label={`${canManageSetupProfiles ? 'Edit' : 'View'} ${p.name}`} onClick={() => navigate(`/setup-profiles/${p.profileId}`)}>
                        {canManageSetupProfiles ? <Pencil /> : <Eye />}
                      </Button>
                    </Tooltip>
                    {canManageSetupProfiles && (
                      <Tooltip content="Delete">
                        <Button variant="ghost" size="icon" aria-label={`Delete ${p.name}`} className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive" onClick={() => setDeleting(p)}>
                          <Trash2 />
                        </Button>
                      </Tooltip>
                    )}
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      {deleting && (
        <ConfirmImpactDialog
          copy={{ confirmTitle: `Delete ${deleting.name}?`, impact: deleteImpact(targetedNames(deleting.targeting, locations)), confirmLabel: 'Delete', destructive: true }}
          busy={deletingBusy}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void handleDelete()}
          titleId="delete-setup-profile-title"
          details={(
            <ul className="max-h-32 list-disc space-y-1 overflow-y-auto pl-4 text-sm">
              {targetedNames(deleting.targeting, locations).map((n) => <li key={n}>{n}</li>)}
            </ul>
          )}
        />
      )}
    </div>
  );
}
