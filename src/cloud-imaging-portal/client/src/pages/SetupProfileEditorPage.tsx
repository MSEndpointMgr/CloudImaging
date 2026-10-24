import { Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, ChevronRight, Copy, Crosshair, Database, Eye, FileCode, History, Info, Pencil, Plus, RefreshCw, Save, ShieldCheck, SquareTerminal, Trash2, Undo2 } from 'lucide-react';
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
import { ProfileHistoryDialog } from '../components/setup-profiles/ProfileHistoryDialog.tsx';
import { ScriptDialog } from '../components/setup-profiles/ScriptDialog.tsx';
import { ScriptsTable } from '../components/setup-profiles/ScriptsTable.tsx';
import { SectionCard, SettingRow } from '../components/setup-profiles/SectionCard.tsx';
import { TargetingSection } from '../components/setup-profiles/TargetingSection.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatTimestamp } from '../lib/utils.ts';
import {
  FAILURE_HANDLING_LABELS,
  MAX_REGISTRY_VALUES,
  MAX_SCRIPTS,
  SetupProfileApiError,
  answerFileProblems,
  answerFileSizeLabel,
  createSetupProfile,
  deleteImpact,
  deleteSetupProfile,
  draftChanges,
  draftFrom,
  duplicateName,
  emptyDraft,
  fetchSetupProfile,
  fetchSetupProfileContent,
  fetchSetupProfileVersion,
  fetchSetupProfiles,
  orderedScripts,
  profileLocationIds,
  readOnlyVersionBanner,
  registryValuePath,
  registryValueTooltip,
  renumberScripts,
  restoreImpact,
  restoreSetupProfileVersion,
  saveSetupProfile,
  scriptsWithoutLocationsProblem,
  targetedNames,
  targetingProblem,
  upsertScript,
  withEffectiveLocations,
  type SetupFailureHandling,
  type SetupProfileDetail,
  type SetupProfileDraft,
  type SetupProfileSummary,
  type SetupRegistryValue,
  type SetupScript,
} from '../lib/setupProfiles.ts';

const FAILURE_HANDLING_OPTIONS = (Object.keys(FAILURE_HANDLING_LABELS) as SetupFailureHandling[]).map((value) => ({ value, label: FAILURE_HANDLING_LABELS[value] }));

type RegistryDialog = { mode: 'add' } | { mode: 'edit'; index: number } | null;
type ScriptDialogState = { mode: 'add' } | { mode: 'edit'; index: number } | null;

// Its XML parser stays out of the editor's bundle until someone opens the answer file.
const AnswerFileDialog = lazy(() => import('../components/setup-profiles/AnswerFileDialog.tsx'));

/** Where a confirmed leave goes; Duplicate carries router state. */
type PendingLeave = { to: string; state?: unknown } | null;

/**
 * Create, edit or view one setup profile. Viewers get the same layout as text. Saving with
 * changed content creates a new version; name and targeting changes do not.
 */
export default function SetupProfileEditorPage(): React.ReactElement {
  const { profileId, version } = useParams<{ profileId: string; version?: string }>();
  const isNew = profileId === undefined;
  // A version route shows that version's content read-only.
  const viewedVersion = version === undefined ? null : Number(version);
  const navigate = useNavigate();
  const location = useLocation();
  const duplicateOf = (location.state as { duplicateOf?: string } | null)?.duplicateOf;
  const { notify } = useToast();
  const { canManageSetupProfiles, isAdministrator } = useAuth();
  const readOnly = !canManageSetupProfiles || viewedVersion !== null;

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
  // The answer file text the server refused, so Save stays off until it changes.
  const [refusedAnswerFile, setRefusedAnswerFile] = useState<string | null>(null);
  const [registryDialog, setRegistryDialog] = useState<RegistryDialog>(null);
  const [scriptDialog, setScriptDialog] = useState<ScriptDialogState>(null);
  const [answerDialog, setAnswerDialog] = useState<{ serverProblems: { code: string; line: number; message: string }[] } | null>(null);
  const [pendingLeave, setPendingLeave] = useState<PendingLeave>(null);
  const [targetingTouched, setTargetingTouched] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [retention, setRetention] = useState<number | null>(null);
  const [restoringViewed, setRestoringViewed] = useState(false);
  const [restoreBusy, setRestoreBusy] = useState(false);
  // Texts uploaded or loaded in this editor, by hash, so a dialog reopens without another download.
  const texts = useRef(new Map<string, string>());

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    setSaveError(null);
    try {
      const [list, locationsRes, current, viewed] = await Promise.all([
        fetchSetupProfiles(),
        apiFetchWithRetry('/api/locations', { credentials: 'include' }),
        isNew ? (duplicateOf ? fetchSetupProfile(duplicateOf) : Promise.resolve(null)) : fetchSetupProfile(profileId),
        !isNew && viewedVersion !== null ? fetchSetupProfileVersion(profileId, viewedVersion) : Promise.resolve(null),
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
        const shown = viewed ? draftFrom({ ...current, content: viewed.content }) : draftFrom(current);
        setSaved(shown);
        setDraft(shown);
      }
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load the setup profile.');
    } finally {
      setLoading(false);
    }
  }, [isNew, duplicateOf, profileId, viewedVersion]);

  useEffect(() => { void load(); }, [load]);

  // A duplicate's texts still belong to the profile it was copied from until it is saved.
  const textSource = detail?.profileId ?? duplicatedFrom?.profileId;
  const loadText = useCallback(async (sha256: string): Promise<string> => {
    const cached = texts.current.get(sha256);
    if (cached !== undefined) return cached;
    if (!textSource) throw new Error('Could not load the text.');
    const text = await fetchSetupProfileContent(textSource, sha256);
    texts.current.set(sha256, text);
    return text;
  }, [textSource]);
  const loadScriptText = useCallback((script: SetupScript) => loadText(script.contentSha256), [loadText]);

  const allLocationIds = useMemo(() => locations.map((l) => l.locationId), [locations]);
  const profileLocations = useMemo(() => {
    const ids = new Set(profileLocationIds(draft.targeting, allLocationIds));
    return locations.filter((l) => ids.has(l.locationId));
  }, [draft.targeting, allLocationIds, locations]);

  const changes = draftChanges(saved, draft);
  const dirty = !readOnly && changes.dirty;
  const targetProblem = targetingProblem(draft.targeting);
  const nameProblem = draft.name.trim().length === 0 || draft.name.trim().length > 100 ? 'Enter a profile name of 1 to 100 characters.' : null;
  const otherNames = useMemo(() => profiles.filter((p) => p.profileId !== profileId).map((p) => p.name.toLowerCase()), [profiles, profileId]);
  const duplicateNameProblem = otherNames.includes(draft.name.trim().toLowerCase()) ? `A setup profile named "${draft.name.trim()}" already exists. Choose another name.` : null;
  const answerFileProblem = refusedAnswerFile !== null && draft.content.answerFile?.contentSha256 === refusedAnswerFile
    ? 'The answer file has problems. Fix them before saving.'
    : null;
  const scriptLocationsProblem = scriptsWithoutLocationsProblem(draft.content.scripts, draft.targeting, allLocationIds);
  const blocked = targetProblem ?? nameProblem ?? duplicateNameProblem ?? answerFileProblem ?? scriptLocationsProblem;

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

  // Only Administrators can read the configuration, so others see the history note without the number.
  const openHistory = async () => {
    setHistoryOpen(true);
    if (!isAdministrator || retention !== null) return;
    try {
      const res = await apiFetchWithRetry('/api/portal-config', { credentials: 'include' });
      if (res.ok) setRetention((await res.json() as { setupProfileVersionRetention?: number }).setupProfileVersionRetention ?? 25);
    } catch {
      // The note reads without the number.
    }
  };

  const restoreViewed = async () => {
    if (!detail || viewedVersion === null) return;
    setRestoreBusy(true);
    try {
      const restored = await restoreSetupProfileVersion(detail.profileId, viewedVersion, detail.currentVersion);
      notify({ status: 'success', title: `Version ${String(viewedVersion)} restored as version ${String(restored.currentVersion)}.` });
      setRestoringViewed(false);
      void navigate(`/setup-profiles/${restored.profileId}`);
    } catch (err) {
      notify({ status: 'error', title: 'The version was not restored.', description: err instanceof Error ? err.message : undefined });
    } finally {
      setRestoreBusy(false);
    }
  };

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
    // Scripts keep only the locations the profile still targets.
    const toSave = { ...draft, content: withEffectiveLocations(draft.content, draft.targeting, allLocationIds) };
    try {
      if (isNew) {
        const created = await createSetupProfile(toSave, duplicatedFrom);
        notify({ status: 'success', title: `Setup profile "${created.name}" created.` });
        setSaved(draftFrom(created));
        setDraft(draftFrom(created));
        void navigate(`/setup-profiles/${created.profileId}`, { replace: true });
        return;
      }
      const result = await saveSetupProfile(detail!.profileId, detail!.currentVersion, toSave, changes.contentChanged);
      notify({
        status: 'success',
        title: result.currentVersion > detail!.currentVersion ? `Saved as version ${String(result.currentVersion)}.` : 'Changes saved.',
      });
      setDetail(result);
      setSaved(draftFrom(result));
      setDraft(draftFrom(result));
      setProfiles((list) => list.map((p) => (p.profileId === result.profileId ? result : p)));
    } catch (err) {
      const error = err instanceof SetupProfileApiError ? err : new SetupProfileApiError('Could not reach the server.', 0);
      setSaveError(error);
      notify({
        status: 'error',
        title: isNew ? 'Could not create the setup profile.' : 'Could not save the setup profile.',
        description: error.message,
      });
      // Refused answer file problems belong next to the file, so the dialog opens with them.
      if (error.errors.length > 0 && draft.content.answerFile) {
        setRefusedAnswerFile(draft.content.answerFile.contentSha256);
        setAnswerDialog({ serverProblems: answerFileProblems(error.errors) });
      }
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
  const scripts = orderedScripts(draft.content.scripts);
  const answerFile = draft.content.answerFile;
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
              className="max-w-md text-xl font-semibold"
            />
          )}
          {duplicateNameProblem && <p className="mt-1 text-sm text-destructive">{duplicateNameProblem}</p>}
          {detail && (
            <p className="mt-2 text-xs text-muted-foreground">
              Version {detail.currentVersion}, last modified {formatTimestamp(detail.lastModifiedAt)}{detail.lastModifiedBy ? ` by ${detail.lastModifiedBy}` : ''}
            </p>
          )}
        </div>
        {detail && (
          <div className="flex shrink-0 gap-2">
            <Button variant="outline" onClick={() => void openHistory()}><History />History</Button>
            {canManageSetupProfiles && viewedVersion === null && (
              <>
                <Button variant="outline" onClick={() => leave('/setup-profiles/new', { duplicateOf: detail.profileId })}><Copy />Duplicate</Button>
                <Button variant="outline" onClick={() => setDeleting(true)}><Trash2 />Delete</Button>
              </>
            )}
          </div>
        )}
      </div>

      {viewedVersion !== null && detail ? (
        <div className="flex items-center gap-3 rounded-md border border-border bg-muted/40 px-4 py-2 text-sm text-muted-foreground">
          <Info className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span className="flex-1">{readOnlyVersionBanner(viewedVersion, detail.currentVersion)}</span>
          {canManageSetupProfiles && viewedVersion !== detail.currentVersion && (
            <Button variant="outline" onClick={() => setRestoringViewed(true)}><Undo2 />Restore</Button>
          )}
        </div>
      ) : readOnly && (
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

      <SectionCard
        icon={SquareTerminal}
        title="Scripts"
        description="PowerShell, run as SYSTEM in the order below within each phase. A device runs the scripts for all locations and the ones for its own location."
        action={!readOnly && (
          <Tooltip content={scripts.length >= MAX_SCRIPTS ? 'A profile can have at most 1000 scripts.' : 'Add a script'}>
            <Button variant="outline" disabled={scripts.length >= MAX_SCRIPTS} onClick={() => setScriptDialog({ mode: 'add' })}><Plus />Add script</Button>
          </Tooltip>
        )}
      >
        {scripts.length === 0 ? (
          <p className="text-sm text-muted-foreground">No scripts.</p>
        ) : (
          <ScriptsTable
            scripts={scripts}
            targeting={draft.targeting}
            locations={locations}
            profileLocations={profileLocations}
            readOnly={readOnly}
            onChange={(next) => setContent({ scripts: next })}
            onEdit={(index) => setScriptDialog({ mode: 'edit', index })}
            onDelete={(index) => setContent({ scripts: renumberScripts(scripts.filter((_, j) => j !== index)) })}
          />
        )}
      </SectionCard>

      <SectionCard icon={FileCode} title="Answer file" description="Optional. Your own unattend.xml for Windows setup, for settings this profile has no control for.">
        <div className="overflow-hidden rounded-md border border-border">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead>File</TableHead>
                <TableHead className="w-[220px]">Passes</TableHead>
                <TableHead className="w-[100px] text-right">Size</TableHead>
                <TableHead className="w-[160px]">Last modified</TableHead>
                {(!readOnly || answerFile) && <TableHead className="w-[96px]">Actions</TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {answerFile ? (
                <TableRow>
                  <TableCell className="font-mono text-xs">unattend.xml</TableCell>
                  <TableCell><p className="truncate">{(answerFile.passes ?? []).join(', ')}</p></TableCell>
                  <TableCell className="text-right tabular-nums">{answerFileSizeLabel(answerFile.sizeBytes)}</TableCell>
                  <TableCell>
                    {answerFile.lastModifiedAt ? (
                      <Tooltip content={answerFile.lastModifiedBy ? `By ${answerFile.lastModifiedBy}` : 'Author unknown'}>
                        <span tabIndex={0} className="tabular-nums">{formatTimestamp(answerFile.lastModifiedAt)}</span>
                      </Tooltip>
                    ) : (
                      <span className="text-muted-foreground">Not saved yet</span>
                    )}
                  </TableCell>
                  {readOnly ? (
                    <TableCell>
                      <Tooltip content="View">
                        <Button variant="ghost" size="icon" aria-label="View the answer file" onClick={() => setAnswerDialog({ serverProblems: [] })}><Eye /></Button>
                      </Tooltip>
                    </TableCell>
                  ) : (
                    <TableCell>
                      <div className="flex gap-1">
                        <Tooltip content="Edit">
                          <Button variant="ghost" size="icon" aria-label="Edit the answer file" onClick={() => setAnswerDialog({ serverProblems: [] })}><Pencil /></Button>
                        </Tooltip>
                        <Tooltip content="Remove">
                          <Button variant="ghost" size="icon" aria-label="Remove the answer file" className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                            onClick={() => setContent({ answerFile: null })}>
                            <Trash2 />
                          </Button>
                        </Tooltip>
                      </div>
                    </TableCell>
                  )}
                </TableRow>
              ) : (
                <TableRow className="hover:bg-transparent">
                  <TableCell className="text-muted-foreground">No answer file</TableCell>
                  <TableCell />
                  <TableCell />
                  <TableCell />
                  {!readOnly && (
                    <TableCell>
                      <Button variant="outline" onClick={() => setAnswerDialog({ serverProblems: [] })}><Plus />Add</Button>
                    </TableCell>
                  )}
                </TableRow>
              )}
            </TableBody>
          </Table>
        </div>
      </SectionCard>

      <SectionCard icon={ShieldCheck} title="Failure handling" description="What happens when a registry value cannot be written, or an Offline script fails or times out.">
        <SettingRow label="Deployment result" help="Specialize scripts never change the result: they run after the portal stops tracking the device." htmlFor="setup-profile-failure-handling">
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
          {saveError.errors.length > 0 && answerFile && (
            <Button variant="outline" onClick={() => setAnswerDialog({ serverProblems: answerFileProblems(saveError.errors) })}><FileCode />Show the problems</Button>
          )}
          {saveError.status === 409 && !isNew && (
            <Button variant="outline" onClick={() => void load()}><RefreshCw />Reload</Button>
          )}
        </div>
      )}

      {dirty && (
        <div className="sticky bottom-0 z-20 flex items-center justify-end gap-3 bg-background/95 py-3 backdrop-blur">
          <p className="text-sm text-muted-foreground">{blocked ?? 'You have unsaved changes.'}</p>
          <Button variant="outline" onClick={() => (isNew ? leave('/setup-profiles') : setDraft(saved))} disabled={saving}><Undo2 />Discard</Button>
          <Button onClick={() => void save()} loading={saving} disabled={Boolean(blocked) || saving}>
            <Save />Save
          </Button>
        </div>
      )}

      {answerDialog && (
        <Suspense fallback={null}>
          <AnswerFileDialog
            answerFile={answerFile}
            loadText={loadText}
            serverProblems={answerDialog.serverProblems}
            readOnly={readOnly}
            onClose={() => setAnswerDialog(null)}
            onApply={(file, text) => {
              texts.current.set(file.contentSha256, text);
              setContent({ answerFile: file.contentSha256 === answerFile?.contentSha256 ? answerFile : file });
              setAnswerDialog(null);
            }}
          />
        </Suspense>
      )}

      {scriptDialog && (
        <ScriptDialog
          script={scriptDialog.mode === 'edit' ? scripts[scriptDialog.index] : undefined}
          others={scripts.filter((_, j) => scriptDialog.mode !== 'edit' || j !== scriptDialog.index)}
          locations={profileLocations}
          loadText={loadScriptText}
          readOnly={readOnly}
          onClose={() => setScriptDialog(null)}
          onApply={(script, text) => {
            texts.current.set(script.contentSha256, text);
            const before = scriptDialog.mode === 'edit' ? scripts[scriptDialog.index] : undefined;
            // An edited script shows as not saved until the profile is saved.
            const changed = !before || before.name !== script.name || before.contentSha256 !== script.contentSha256 || before.runs !== script.runs
              || before.timeoutMinutes !== script.timeoutMinutes || JSON.stringify(before.locations) !== JSON.stringify(script.locations);
            const next = changed ? { ...script, lastModifiedAt: null, lastModifiedBy: null } : script;
            setContent({ scripts: upsertScript(scripts, next, scriptDialog.mode === 'edit' ? scriptDialog.index : undefined) });
            setScriptDialog(null);
          }}
        />
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

      {historyOpen && detail && (
        <ProfileHistoryDialog
          profile={detail}
          retention={retention}
          canRestore={canManageSetupProfiles}
          onClose={() => setHistoryOpen(false)}
          onView={(v) => { setHistoryOpen(false); leave(`/setup-profiles/${detail.profileId}/versions/${String(v)}`); }}
          onRestored={(restored) => {
            setHistoryOpen(false);
            notify({ status: 'success', title: `Restored as version ${String(restored.currentVersion)}.` });
            if (viewedVersion !== null) {
              void navigate(`/setup-profiles/${restored.profileId}`);
              return;
            }
            setDetail(restored);
            setSaved(draftFrom(restored));
            setDraft(draftFrom(restored));
          }}
        />
      )}

      {restoringViewed && detail && viewedVersion !== null && (
        <ConfirmImpactDialog
          icon={Undo2}
          copy={{ confirmTitle: `Restore version ${String(viewedVersion)}?`, impact: restoreImpact(viewedVersion, detail.currentVersion), confirmLabel: 'Restore', destructive: false }}
          busy={restoreBusy}
          onCancel={() => setRestoringViewed(false)}
          onConfirm={() => void restoreViewed()}
          titleId="restore-viewed-version-title"
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
