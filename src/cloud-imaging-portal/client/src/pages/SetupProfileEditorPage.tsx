import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, Info, KeyRound, MapPin, Pencil, Plus, RefreshCw, Save, ShieldAlert, Trash2, Undo2 } from 'lucide-react';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../components/ui/card.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog } from '../components/ConfirmImpactDialog.tsx';
import type { PickerLocation } from '../components/LocationPicker.tsx';
import { TargetingBadges } from '../components/TargetingBadges.tsx';
import { RegistryValueDialog } from '../components/setup-profiles/RegistryValueDialog.tsx';
import { TargetingSection } from '../components/setup-profiles/TargetingSection.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import {
  FAILURE_HANDLING_LABELS,
  MAX_REGISTRY_VALUES,
  SetupProfileApiError,
  createSetupProfile,
  draftChanges,
  draftFrom,
  duplicateName,
  emptyDraft,
  fetchSetupProfile,
  fetchSetupProfiles,
  registryKeyPath,
  registryValueTooltip,
  saveLabel,
  saveSetupProfile,
  targetingProblem,
  type SetupFailureHandling,
  type SetupProfileDetail,
  type SetupProfileDraft,
  type SetupProfileSummary,
  type SetupRegistryValue,
} from '../lib/setupProfiles.ts';

const FAILURE_HANDLING_HELP: Record<SetupFailureHandling, string> = {
  CompleteWithWarnings: 'Failed items are listed on the deployment timeline and the deployment continues.',
  FailDeployment: 'The deployment stops before the device restarts into Windows. Results of Windows setup scripts never change the result.',
};

function SectionCard({ icon: Icon, title, description, action, children }: {
  icon: React.ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' }>;
  title: string;
  description: string;
  action?: React.ReactNode;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <Icon className="h-5 w-5 text-primary" aria-hidden="true" />
              <CardTitle className="text-base">{title}</CardTitle>
            </div>
            <CardDescription>{description}</CardDescription>
          </div>
          {action && <div className="shrink-0">{action}</div>}
        </div>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  );
}

type RegistryDialog = { mode: 'add' } | { mode: 'edit'; index: number } | null;

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
  const [pendingLeave, setPendingLeave] = useState<string | null>(null);

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
      setPendingLeave(url.pathname + url.search);
    };
    const onBeforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    document.addEventListener('click', onClick, true);
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => {
      document.removeEventListener('click', onClick, true);
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [dirty]);

  const leave = (to: string) => {
    if (dirty) setPendingLeave(to);
    else void navigate(to);
  };

  const set = (patch: Partial<SetupProfileDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const setContent = (patch: Partial<SetupProfileDraft['content']>) => setDraft((d) => ({ ...d, content: { ...d.content, ...patch } }));

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
  const title = isNew ? (duplicatedFrom ? 'Duplicate setup profile' : 'New setup profile') : draft.name || detail?.name || 'Setup profile';

  return (
    <div className={`space-y-4${dirty ? ' pb-20' : ''}`}>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex min-w-0 items-center gap-2">
          <Tooltip content="All profiles">
            <Button variant="ghost" size="icon" aria-label="All profiles" onClick={() => leave('/setup-profiles')}><ArrowLeft /></Button>
          </Tooltip>
          <h2 className="truncate text-lg font-semibold">{title}</h2>
        </div>
      </div>

      {readOnly && (
        <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 px-4 py-2 text-sm text-muted-foreground">
          <Info className="h-4 w-4 shrink-0" aria-hidden="true" />
          Read-only
        </div>
      )}

      <SectionCard icon={Pencil} title="Profile" description="The name shown in the profile list, on the Devices page and on the deployment timeline.">
        <div className="space-y-2">
          {readOnly ? (
            <p className="text-sm">{draft.name}</p>
          ) : (
            <>
              <Label htmlFor="setup-profile-name">Name</Label>
              <Input id="setup-profile-name" value={draft.name} maxLength={100} onChange={(e) => set({ name: e.target.value })} className="max-w-md" />
              {duplicateNameProblem && <p className="text-sm text-destructive">{duplicateNameProblem}</p>}
            </>
          )}
          {detail && (
            <p className="text-sm text-muted-foreground">
              Version {detail.currentVersion}, modified {formatDateTime(detail.lastModifiedAt)}{detail.lastModifiedBy ? ` by ${detail.lastModifiedBy}` : ''}
            </p>
          )}
        </div>
      </SectionCard>

      <SectionCard icon={MapPin} title="Targeting" description="Devices get the profile that targets the location on their USB media.">
        {readOnly ? (
          <TargetingBadges all={draft.targeting.allDevices} locationIds={draft.targeting.locationIds} locations={locations} allLabel="All devices" editNoun="profile" />
        ) : (
          <TargetingSection
            targeting={draft.targeting}
            onChange={(targeting) => set({ targeting })}
            profiles={profiles}
            profileId={detail?.profileId}
            locations={locations}
            problem={targetProblem}
          />
        )}
      </SectionCard>

      <SectionCard
        icon={KeyRound}
        title="Registry values"
        description="Written to the applied image in WinPE, before Windows starts for the first time."
        action={!readOnly && (
          <Tooltip content={registryValues.length >= MAX_REGISTRY_VALUES ? 'A profile can have at most 200 registry values.' : 'Add a registry value'}>
            <Button variant="outline" disabled={registryValues.length >= MAX_REGISTRY_VALUES} onClick={() => setRegistryDialog({ mode: 'add' })}><Plus />Add registry value</Button>
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
                  <TableHead className="w-[28%]">Name</TableHead>
                  <TableHead>Value</TableHead>
                  <TableHead className="w-[20%]">Data</TableHead>
                  {!readOnly && <TableHead className="w-[96px]">Actions</TableHead>}
                </TableRow>
              </TableHeader>
              <TableBody>
                {registryValues.map((v, i) => (
                  <TableRow key={v.id ?? `new-${String(i)}`}>
                    <TableCell><p className="truncate font-medium">{v.name}</p></TableCell>
                    <TableCell>
                      <Tooltip content={registryValueTooltip(v)} className="min-w-0 max-w-full">
                        <span tabIndex={0} className="block truncate font-mono text-xs">{registryKeyPath(v)}</span>
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

      <SectionCard icon={ShieldAlert} title="Failure handling" description="What happens to the deployment when a registry value, WinPE script or the answer file fails.">
        {readOnly ? (
          <p className="text-sm">{FAILURE_HANDLING_LABELS[draft.content.failureHandling]}</p>
        ) : (
          <div role="radiogroup" aria-label="Failure handling" className="space-y-3">
            {(['CompleteWithWarnings', 'FailDeployment'] as const).map((value) => (
              <label key={value} className="flex cursor-pointer items-start gap-3">
                <input
                  type="radio"
                  name="setup-profile-failure-handling"
                  value={value}
                  checked={draft.content.failureHandling === value}
                  onChange={() => setContent({ failureHandling: value })}
                  className="mt-1 h-4 w-4 accent-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                />
                <span className="space-y-1">
                  <span className="block text-sm font-medium">{FAILURE_HANDLING_LABELS[value]}</span>
                  <span className="block text-sm text-muted-foreground">{FAILURE_HANDLING_HELP[value]}</span>
                </span>
              </label>
            ))}
          </div>
        )}
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
        <div className="fixed inset-x-0 bottom-0 z-30 border-t border-border bg-background/95 backdrop-blur">
          <div className="flex items-center justify-end gap-4 px-6 py-3">
            <p className="mr-auto text-sm text-muted-foreground">{blocked ?? 'You have unsaved changes.'}</p>
            <Button variant="outline" onClick={() => (isNew ? leave('/setup-profiles') : setDraft(saved))} disabled={saving}><Undo2 />Discard</Button>
            <Button onClick={() => void save()} loading={saving} disabled={Boolean(blocked) || saving}>
              <Save />{saveLabel(detail?.currentVersion ?? 0, contentChanged)}
            </Button>
          </div>
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
            const to = pendingLeave;
            setPendingLeave(null);
            setDraft(saved);
            // Navigating after the draft resets lets the leave guard see a clean page.
            setTimeout(() => navigate(to), 0);
          }}
          titleId="discard-setup-profile-title"
        />
      )}
    </div>
  );
}
