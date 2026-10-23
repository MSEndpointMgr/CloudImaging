import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, ChevronRight, Copy, Crosshair, Database, Info, Pencil, Plus, RefreshCw, ShieldCheck, Trash2 } from 'lucide-react';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import type { PickerLocation } from '../components/LocationPicker.tsx';
import { TargetingBadges } from '../components/TargetingBadges.tsx';
import { RegistryValueDialog } from '../components/setup-profiles/RegistryValueDialog.tsx';
import { SectionCard, SettingRow } from '../components/setup-profiles/SectionCard.tsx';
import { TargetingSection } from '../components/setup-profiles/TargetingSection.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatTimestamp } from '../lib/utils.ts';
import {
  FAILURE_HANDLING_LABELS,
  MAX_REGISTRY_VALUES,
  SetupProfileApiError,
  createSetupProfile,
  deleteImpact,
  deleteSetupProfile,
  draftChanges,
  draftFrom,
  duplicateName,
  emptyDraft,
  fetchSetupProfile,
  fetchSetupProfiles,
  registryValuePath,
  registryValueTooltip,
  saveLabel,
  saveSetupProfile,
  targetedNames,
  targetingProblem,
  type SetupFailureHandling,
  type SetupProfileDetail,
  type SetupProfileDraft,
  type SetupProfileSummary,
  type SetupRegistryValue,
} from '../lib/setupProfiles.ts';

const FAILURE_HANDLING_OPTIONS = (Object.keys(FAILURE_HANDLING_LABELS) as SetupFailureHandling[]).map((value) => ({ value, label: FAILURE_HANDLING_LABELS[value] }));

type RegistryDialog = { mode: 'add' } | { mode: 'edit'; index: number } | null;

/** Where a confirmed leave goes; Duplicate carries router state. */
type PendingLeave = { to: string; state?: unknown } | null;

/**
 * Create, edit or view one setup profile. Viewers get the same layout as text. Saving with
 * changed content creates a new version; name and targeting changes do not.
 */
export default function SetupProfileEditorPage(): React.ReactElement {
  const { profileId } = useParams<{ profileId: string }>();
  const isNew = profileId === undefined;
  const navigate = useNavigate();
  const location = useLocation();
  const duplicateOf = (location.state as { duplicateOf?: string } | null)?.duplicateOf;
  const { notify } = useToast();
  const { canManageSetupProfiles } = useAuth();
  const readOnly = !canManageSetupProfiles;

  const [profiles, setProfiles] = useState<SetupProfileSummary[]>([]);
  const [locations, setLocations] = useState<PickerLocation[]>([]);
  const [detail, setDetail] = useState<SetupProfileDetail | null>(null);
  const [saved, setSaved] = useState<SetupProfileDraft>(emptyDraft);
  const [draft, setDraft] = useState<SetupProfileDraft>(emptyDraft);
  const [duplicatedFrom, setDuplicatedFrom] = useState<{ profileId: string; version: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<SetupProfileApiError | null>(null);
  const [registryDialog, setRegistryDialog] = useState<RegistryDialog>(null);
  const [pendingLeave, setPendingLeave] = useState<PendingLeave>(null);
  const [targetingTouched, setTargetingTouched] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deletingBusy, setDeletingBusy] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    setSaveError(null);
    try {
      const [list, locationsRes, current] = await Promise.all([
        fetchSetupProfiles(),
        apiFetchWithRetry('/api/locations', { credentials: 'include' }),
        isNew ? (duplicateOf ? fetchSetupProfile(duplicateOf) : Promise.resolve(null)) : fetchSetupProfile(profileId),
      ]);
      setProfiles(list);
      if (locationsRes.ok) setLocations((await locationsRes.json() as PickerLocation[]).sort((a, b) => a.name.localeCompare(b.name)));
      if (isNew) {
        // A duplicate starts from the source's content and a free name, with no targeting.
        const start = current
          ? { name: duplicateName(current.name, list.map((p) => p.name)), targeting: { allDevices: false, locationIds: [] }, content: draftFrom(current).content }
          : emptyDraft();
        setDetail(null);
        setDuplicatedFrom(current ? { profileId: current.profileId, version: current.currentVersion } : null);
        setSaved(current ? { ...emptyDraft(), content: start.content } : emptyDraft());
        setDraft(start);
      } else if (current) {
        setDetail(current);
        setSaved(draftFrom(current));
        setDraft(draftFrom(current));
      }
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load the setup profile.');
    } finally {
      setLoading(false);
    }
  }, [isNew, duplicateOf, profileId]);

  useEffect(() => { void load(); }, [load]);

  const changes = draftChanges(saved, draft);
  // A new profile always saves as version 1, even when it starts from a duplicate's content.
  const contentChanged = isNew || changes.contentChanged;
  const dirty = !readOnly && changes.dirty;
  const targetProblem = targetingProblem(draft.targeting);
  const nameProblem = draft.name.trim().length === 0 || draft.name.trim().length > 100 ? 'Enter a profile name of 1 to 100 characters.' : null;
  const otherNames = useMemo(() => profiles.filter((p) => p.profileId !== profileId).map((p) => p.name.toLowerCase()), [profiles, profileId]);
  const duplicateNameProblem = otherNames.includes(draft.name.trim().toLowerCase()) ? `A setup profile named "${draft.name.trim()}" already exists. Choose another name.` : null;
  const blocked = targetProblem ?? nameProblem ?? duplicateNameProblem;

  // Leaving with unsaved changes asks first. In-app links are caught before the router sees them;
  // closing or reloading the tab gets the browser's own prompt.
  useEffect(() => {
    if (!dirty) return;
    const onClick = (event: MouseEvent) => {
      const anchor = (event.target as Element | null)?.closest?.('a[href]');
      if (!anchor || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return;
      const url = new URL((anchor as HTMLAnchorElement).href);
      if (url.origin !== window.location.origin || url.pathname === window.location.pathname) return;
      event.preventDefault();
      event.stopPropagation();
      setPendingLeave({ to: url.pathname + url.search });
    };
    const onBeforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    document.addEventListener('click', onClick, true);
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => {
      document.removeEventListener('click', onClick, true);
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [dirty]);

  const leave = (to: string, state?: unknown) => {
    if (dirty) setPendingLeave({ to, state });
    else void navigate(to, { state });
  };

  const set = (patch: Partial<SetupProfileDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const setContent = (patch: Partial<SetupProfileDraft['content']>) => setDraft((d) => ({ ...d, content: { ...d.content, ...patch } }));

  const handleDelete = async () => {
    if (!detail) return;
    setDeletingBusy(true);
    try {
      await deleteSetupProfile(detail.profileId);
      notify({ status: 'success', title: `Setup profile "${detail.name}" deleted.` });
      setDeleting(false);
      setDraft(saved);
      // Navigating after the draft resets lets the leave guard see a clean page.
      setTimeout(() => navigate('/setup-profiles'), 0);
    } catch (err) {
      notify({ status: 'error', title: 'Failed to delete the setup profile.', description: err instanceof Error ? err.message : undefined });
    } finally {
      setDeletingBusy(false);
    }
  };

  const save = async () => {
    if (blocked) return;
    setSaving(true);
    setSaveError(null);
    try {
      if (isNew) {
        const created = await createSetupProfile(draft, duplicatedFrom);
        notify({ status: 'success', title: `Setup profile "${created.name}" created.` });
        setSaved(draftFrom(created));
        setDraft(draftFrom(created));
        void navigate(`/setup-profiles/${created.profileId}`, { replace: true });
        return;
      }
      const result = await saveSetupProfile(detail!.profileId, detail!.currentVersion, draft, changes.contentChanged);
      notify({
        status: 'success',
        title: result.currentVersion > detail!.currentVersion ? `Saved as version ${String(result.currentVersion)}.` : 'Changes saved.',
      });
      setDetail(result);
      setSaved(draftFrom(result));
      setDraft(draftFrom(result));
      setProfiles((list) => list.map((p) => (p.profileId === result.profileId ? result : p)));
    } catch (err) {
      setSaveError(err instanceof SetupProfileApiError ? err : new SetupProfileApiError('Could not reach the server.', 0));
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  if (loadError) {
    return (
      <div className="space-y-4">
        <Button variant="outline" onClick={() => navigate('/setup-profiles')}><ArrowLeft />All profiles</Button>
        <div className="rounded-md border border-destructive/50 bg-destructive/10 p-4">
          <p className="text-sm text-destructive">{loadError}</p>
          <Button variant="link" onClick={() => void load()}>Try again</Button>
        </div>
      </div>
    );
  }

  const registryValues = draft.content.registryValues;
  const crumb = isNew ? 'New profile' : detail?.name ?? 'Setup profile';

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <nav aria-label="Breadcrumb" className="mb-2 flex items-center gap-1 text-xs text-muted-foreground">
            <Link to="/setup-profiles" className="hover:text-foreground hover:underline">Setup Profiles</Link>
            <ChevronRight className="h-3 w-3 shrink-0" aria-hidden="true" />
            <span className="truncate">{crumb}</span>
          </nav>
          {readOnly ? (
            <h2 className="truncate text-xl font-semibold">{draft.name}</h2>
          ) : (
            <Input
              aria-label="Profile name"
              placeholder="Profile name"
              value={draft.name}
              maxLength={100}
              onChange={(e) => set({ name: e.target.value })}
              className="h-9 max-w-md text-xl font-semibold"
            />
          )}
          {duplicateNameProblem && <p className="mt-1 text-sm text-destructive">{duplicateNameProblem}</p>}
          {detail && (
            <p className="mt-2 text-xs text-muted-foreground">
              Version {detail.currentVersion}, last modified {formatTimestamp(detail.lastModifiedAt)}{detail.lastModifiedBy ? ` by ${detail.lastModifiedBy}` : ''}
            </p>
          )}
        </div>
        {detail && canManageSetupProfiles && (
          <div className="flex shrink-0 gap-2">
            <Button variant="outline" onClick={() => leave('/setup-profiles/new', { duplicateOf: detail.profileId })}><Copy />Duplicate</Button>
            <Button variant="outline" onClick={() => setDeleting(true)}><Trash2 />Delete</Button>
          </div>
        )}
      </div>

      {readOnly && (
        <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 px-4 py-2 text-sm text-muted-foreground">
          <Info className="h-4 w-4 shrink-0" aria-hidden="true" />
          Read-only
        </div>
      )}

      <SectionCard icon={Crosshair} title="Targeting" description="Which devices get this profile, based on the location set on the USB media they started from.">
        {readOnly ? (
          <TargetingBadges all={draft.targeting.allDevices} locationIds={draft.targeting.locationIds} locations={locations} allLabel="All devices" editNoun="profile" />
        ) : (
          <TargetingSection
            targeting={draft.targeting}
            onChange={(targeting) => { setTargetingTouched(true); set({ targeting }); }}
            profiles={profiles}
            profileId={detail?.profileId}
            locations={locations}
            problem={targetingTouched ? targetProblem : null}
          />
        )}
      </SectionCard>

      <SectionCard
        icon={Database}
        title="Registry values"
        description="Written before Windows starts, so they are in place before Autopilot and Intune policy."
        action={!readOnly && (
          <Tooltip content={registryValues.length >= MAX_REGISTRY_VALUES ? 'A profile can have at most 200 registry values.' : 'Add a registry value'}>
            <Button variant="outline" disabled={registryValues.length >= MAX_REGISTRY_VALUES} onClick={() => setRegistryDialog({ mode: 'add' })}><Plus />Add value</Button>
          </Tooltip>
        )}
      >
        {registryValues.length === 0 ? (
          <p className="text-sm text-muted-foreground">No registry values.</p>
        ) : (
          <div className="overflow-hidden rounded-md border border-border">
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <TableHead className="w-[30%]">Name</TableHead>
                  <TableHead>Value</TableHead>
                  <TableHead className="w-[80px]">Data</TableHead>
                  {!readOnly && <TableHead className="w-[96px]">Actions</TableHead>}
                </TableRow>
              </TableHeader>
              <TableBody>
                {registryValues.map((v, i) => (
                  <TableRow key={v.id ?? `new-${String(i)}`}>
                    <TableCell><p className="truncate">{v.name}</p></TableCell>
                    <TableCell>
                      <Tooltip content={registryValueTooltip(v)} className="min-w-0 max-w-full">
                        <span tabIndex={0} className="block truncate font-mono text-xs text-muted-foreground">{registryValuePath(v)}</span>
                      </Tooltip>
                    </TableCell>
                    <TableCell><p className="truncate font-mono text-xs">{v.type === 'MultiString' ? v.data.split('\n').join(', ') : v.data}</p></TableCell>
                    {!readOnly && (
                      <TableCell>
                        <div className="flex gap-1">
                          <Tooltip content="Edit">
                            <Button variant="ghost" size="icon" aria-label={`Edit ${v.name}`} onClick={() => setRegistryDialog({ mode: 'edit', index: i })}><Pencil /></Button>
                          </Tooltip>
                          <Tooltip content="Delete">
                            <Button variant="ghost" size="icon" aria-label={`Delete ${v.name}`} className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                              onClick={() => setContent({ registryValues: registryValues.filter((_, j) => j !== i) })}>
                              <Trash2 />
                            </Button>
                          </Tooltip>
                        </div>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </SectionCard>

      <SectionCard icon={ShieldCheck} title="Failure handling" description="What happens when a registry value cannot be written, or a WinPE script fails or times out.">
        <SettingRow label="Deployment result" help="Windows setup scripts never change the result: they run after the portal stops tracking the device." htmlFor="setup-profile-failure-handling">
          {readOnly ? (
            <p className="text-sm">{FAILURE_HANDLING_LABELS[draft.content.failureHandling]}</p>
          ) : (
            <Select
              id="setup-profile-failure-handling"
              value={draft.content.failureHandling}
              onValueChange={(v) => setContent({ failureHandling: v as SetupFailureHandling })}
              options={FAILURE_HANDLING_OPTIONS}
              wrapperClassName="w-[300px]"
            />
          )}
        </SettingRow>
      </SectionCard>

      {saveError && (
        <div role="alert" className="space-y-2 rounded-md border border-destructive/50 bg-destructive/10 p-4">
          <p className="text-sm font-medium text-destructive">{saveError.message}</p>
          {saveError.errors.length > 0 && (
            <ul className="space-y-1">
              {saveError.errors.map((e, i) => (
                <li key={`${e.code}-${String(i)}`} className="text-sm text-destructive">{e.line != null ? `Line ${String(e.line)}: ` : ''}{e.message}</li>
              ))}
            </ul>
          )}
          {saveError.status === 409 && !isNew && (
            <Button variant="outline" onClick={() => void load()}><RefreshCw />Reload</Button>
          )}
        </div>
      )}

      {dirty && (
        <div className="sticky bottom-0 z-20 flex items-center justify-end gap-3 bg-background/95 py-3 backdrop-blur">
          <p className="text-sm text-muted-foreground">{blocked ?? 'You have unsaved changes.'}</p>
          <Button variant="outline" onClick={() => (isNew ? leave('/setup-profiles') : setDraft(saved))} disabled={saving}>Discard</Button>
          <Button onClick={() => void save()} loading={saving} disabled={Boolean(blocked) || saving}>
            {saveLabel(detail?.currentVersion ?? 0, contentChanged)}
          </Button>
        </div>
      )}

      {registryDialog && (
        <RegistryValueDialog
          value={registryDialog.mode === 'edit' ? registryValues[registryDialog.index] : undefined}
          others={registryValues.filter((_, j) => registryDialog.mode !== 'edit' || j !== registryDialog.index)}
          onClose={() => setRegistryDialog(null)}
          onApply={(value: SetupRegistryValue) => {
            setContent({
              registryValues: registryDialog.mode === 'edit'
                ? registryValues.map((v, j) => (j === registryDialog.index ? value : v))
                : [...registryValues, value],
            });
            setRegistryDialog(null);
          }}
        />
      )}

      {pendingLeave && (
        <ConfirmImpactDialog
          copy={{ confirmTitle: 'Discard your changes?', impact: 'Your changes to this setup profile have not been saved and will be lost.', confirmLabel: 'Discard', destructive: true }}
          busy={false}
          onCancel={() => setPendingLeave(null)}
          onConfirm={() => {
            const target = pendingLeave;
            setPendingLeave(null);
            setDraft(saved);
            // Navigating after the draft resets lets the leave guard see a clean page.
            setTimeout(() => navigate(target.to, { state: target.state }), 0);
          }}
          titleId="discard-setup-profile-title"
        />
      )}

      {deleting && detail && (
        <ConfirmImpactDialog
          copy={{ confirmTitle: `Delete ${detail.name}?`, impact: deleteImpact(targetedNames(detail.targeting, locations)), confirmLabel: 'Delete', destructive: true }}
          busy={deletingBusy}
          onCancel={() => setDeleting(false)}
          onConfirm={() => void handleDelete()}
          titleId="delete-setup-profile-title"
          details={(
            <ul className="max-h-32 list-disc space-y-1 overflow-y-auto pl-4 text-sm">
              {targetedNames(detail.targeting, locations).map((n) => <li key={n}>{n}</li>)}
            </ul>
          )}
        />
      )}
    </div>
  );
}
