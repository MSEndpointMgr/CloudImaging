import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CircleX, Info, RotateCw, Upload, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { CodeTextArea } from '../ui/code-text-area.tsx';
import { Input } from '../ui/input.tsx';
import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { LocationPicker, type PickerLocation } from '../LocationPicker.tsx';
import { ScriptEnvironmentDialog } from './ScriptEnvironmentDialog.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  DEFAULT_TIMEOUT_MINUTES,
  MAX_SCRIPT_BYTES,
  SCRIPT_LOCATIONS_OPTIONS,
  SCRIPT_NOT_UPLOADED_MESSAGE,
  SCRIPT_RUNS_OPTIONS,
  SetupProfileApiError,
  TIMEOUT_OPTIONS,
  loadingScriptLabel,
  scriptSizeLabel,
  scriptTextProblem,
  scriptTooLargeMessage,
  uploadProgressLabel,
  uploadSetupProfileContent,
  utf8Length,
  validateScript,
  type SetupScript,
  type SetupScriptPhase,
} from '../../lib/setupProfiles.ts';

type UploadState = { kind: 'idle' } | { kind: 'uploading'; loaded: number; total: number } | { kind: 'failed'; message: string };

/**
 * Add, edit or view one script. Opening an existing script loads its text first. Done uploads the
 * text on its own (only when it changed) and then closes; the profile's own Save creates the
 * version. Problems use the same rules as Imaging Core and show once the operator presses Done.
 */
export function ScriptDialog({ script, others, locations, loadText, onClose, onApply, readOnly = false, upload = uploadSetupProfileContent }: {
  /** The script to edit; omit to add one. */
  script?: SetupScript;
  /** The profile's other scripts, to keep names unique. */
  others: SetupScript[];
  /** The locations the script may run at: the profile's targeting, or every location for All devices. */
  locations: PickerLocation[];
  /** Loads the text of an existing script. */
  loadText: (script: SetupScript) => Promise<string>;
  onClose: () => void;
  onApply: (script: SetupScript, text: string) => void;
  readOnly?: boolean;
  upload?: typeof uploadSetupProfileContent;
}): React.ReactElement {
  const [draft, setDraft] = useState<SetupScript>(() => script ?? {
    id: null, name: '', runs: 'WinPE', order: 0, timeoutMinutes: DEFAULT_TIMEOUT_MINUTES, locations: { all: true, locationIds: [] }, contentSha256: '', sizeBytes: 0,
  });
  const existing = Boolean(script?.contentSha256);
  const [text, setText] = useState<string | null>(existing ? null : '');
  const [original, setOriginal] = useState<string | null>(existing ? null : '');
  const [loadError, setLoadError] = useState<string | null>(null);
  const [fileProblem, setFileProblem] = useState<string | null>(null);
  const [state, setState] = useState<UploadState>({ kind: 'idle' });
  const [attempted, setAttempted] = useState(false);
  const [environmentOpen, setEnvironmentOpen] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const abort = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!script?.contentSha256) return;
    let live = true;
    loadText(script)
      .then((value) => { if (live) { setText(value); setOriginal(value); } })
      .catch((err: unknown) => { if (live) setLoadError(err instanceof Error ? err.message : 'Could not load the script.'); });
    return () => { live = false; };
  }, [script, loadText]);

  useEffect(() => () => abort.current?.abort(), []);

  const set = (patch: Partial<SetupScript>) => setDraft((d) => ({ ...d, ...patch }));
  const allowed = useMemo(() => new Set(locations.map((l) => l.locationId)), [locations]);
  const selected = draft.locations.locationIds.filter((id) => allowed.has(id));
  const bytes = useMemo(() => (text === null ? script?.sizeBytes ?? 0 : utf8Length(text)), [text, script]);
  const tooLarge = bytes > MAX_SCRIPT_BYTES;
  const textProblem = text === null || tooLarge ? null : scriptTextProblem(text);
  const problems = [...validateScript({ ...draft, locations: { all: draft.locations.all, locationIds: selected } }, others), ...(textProblem ? [textProblem] : [])];
  const uploading = state.kind === 'uploading';

  const changeText = (value: string) => {
    setText(value);
    setFileProblem(null);
    if (state.kind === 'failed') setState({ kind: 'idle' });
  };

  const readFile = async (file: File | undefined) => {
    if (!file) return;
    if (file.size > MAX_SCRIPT_BYTES + 3) {
      setFileProblem(scriptTooLargeMessage(file.size));
      return;
    }
    changeText((await file.text()).replace(/^\uFEFF/, '').replace(/\r\n/g, '\n'));
  };

  const apply = async () => {
    setAttempted(true);
    if (text === null || tooLarge || problems.length > 0) return;
    const next = { ...draft, name: draft.name.trim(), locations: draft.locations.all ? { all: true, locationIds: [] } : { all: false, locationIds: selected } };
    if (text === original && draft.contentSha256) {
      onApply(next, text);
      return;
    }
    const controller = new AbortController();
    abort.current = controller;
    setState({ kind: 'uploading', loaded: 0, total: utf8Length(text) });
    try {
      const result = await upload('script', text, (loaded, total) => setState({ kind: 'uploading', loaded, total }), controller.signal);
      onApply({ ...next, contentSha256: result.sha256, sizeBytes: result.sizeBytes }, text);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const refused = err instanceof SetupProfileApiError && err.status >= 400 && err.status < 500;
      setState({ kind: 'failed', message: refused ? err.message : SCRIPT_NOT_UPLOADED_MESSAGE });
    } finally {
      abort.current = null;
    }
  };

  const cancel = () => {
    abort.current?.abort();
    onClose();
  };

  const status = (() => {
    if (state.kind === 'uploading') {
      const percent = state.total > 0 ? Math.round((state.loaded / state.total) * 100) : 0;
      return (
        <div className="flex min-w-0 flex-1 items-center gap-3">
          <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted" role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
            <div className="h-full rounded-full bg-primary transition-[width]" style={{ width: `${String(percent)}%` }} />
          </div>
          <span className="shrink-0 text-sm text-muted-foreground">{uploadProgressLabel(state.loaded, state.total)}</span>
        </div>
      );
    }
    const error = state.kind === 'failed' ? state.message : fileProblem ?? (tooLarge ? scriptTooLargeMessage(bytes) : loadError);
    if (error) return <p role="alert" className="flex items-center gap-2 text-sm text-destructive"><CircleX className="h-4 w-4 shrink-0" aria-hidden="true" />{error}</p>;
    if (text === null) return <p className="text-sm text-muted-foreground" aria-live="polite">{loadingScriptLabel(script?.sizeBytes ?? 0)}</p>;
    if (script?.lastModifiedAt) return <p className="text-xs text-muted-foreground">Last modified {formatTimestamp(script.lastModifiedAt)}{script.lastModifiedBy ? ` by ${script.lastModifiedBy}` : ''}</p>;
    return null;
  })();

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="script-dialog-title">
      <div className="flex max-h-full w-full max-w-3xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="script-dialog-title" className="text-base font-semibold">{readOnly ? 'Script' : script ? 'Edit script' : 'Add script'}</h2>
            <p className="text-sm text-muted-foreground">Runs as SYSTEM on devices that get this profile, from the locations you choose.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={cancel} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          <div className="grid grid-cols-[minmax(0,1fr)_200px_140px] gap-4">
            <div className="space-y-2">
              <Label htmlFor="script-name">Name</Label>
              <Input id="script-name" value={draft.name} maxLength={100} disabled={readOnly || uploading} onChange={(e) => set({ name: e.target.value })} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="script-runs">Runs</Label>
              <Select id="script-runs" value={draft.runs} disabled={readOnly || uploading} onValueChange={(v) => set({ runs: v as SetupScriptPhase })} options={SCRIPT_RUNS_OPTIONS} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="script-timeout">Timeout</Label>
              <Select id="script-timeout" value={String(draft.timeoutMinutes)} disabled={readOnly || uploading} onValueChange={(v) => set({ timeoutMinutes: Number(v) })} options={TIMEOUT_OPTIONS} />
            </div>
          </div>

          <div className="space-y-3">
            <div className="w-[360px] max-w-full space-y-2">
              <Label htmlFor="script-locations">Locations</Label>
              <Select
                id="script-locations"
                value={draft.locations.all ? 'all' : 'selected'}
                disabled={readOnly || uploading}
                onValueChange={(v) => set({ locations: { all: v === 'all', locationIds: draft.locations.locationIds } })}
                options={SCRIPT_LOCATIONS_OPTIONS}
              />
            </div>
            {!draft.locations.all && !readOnly && (
              <LocationPicker
                idPrefix="script-loc"
                locations={locations}
                selected={selected}
                onChange={(locationIds) => set({ locations: { all: false, locationIds } })}
                footerNote="Only locations this profile targets"
              />
            )}
            {!draft.locations.all && readOnly && (
              <p className="text-sm">{locations.filter((l) => selected.includes(l.locationId)).map((l) => l.name).join(', ') || 'No locations'}</p>
            )}
          </div>

          <div className="space-y-2">
            <div className="flex items-center gap-4">
              <div className="min-w-0 flex-1 space-y-1">
                <div className="flex items-center gap-1">
                  <Label htmlFor="script-content">PowerShell</Label>
                  <Tooltip content="Script environment">
                    <Button variant="ghost" size="icon" aria-label="Script environment" onClick={() => setEnvironmentOpen(true)}><Info /></Button>
                  </Tooltip>
                </div>
                <p id="script-content-help" className="text-xs text-muted-foreground">Exit code 0 is success. Scripts cannot restart the device.</p>
              </div>
              {!readOnly && (
                <>
                  <Button variant="outline" disabled={uploading || text === null} onClick={() => fileInput.current?.click()}><Upload />Upload file</Button>
                  <input ref={fileInput} type="file" accept=".ps1,.psm1,.txt" className="hidden" aria-hidden="true" tabIndex={-1}
                    onChange={(e) => { void readFile(e.target.files?.[0]); e.target.value = ''; }} />
                </>
              )}
            </div>
            <CodeTextArea id="script-content" label="PowerShell" value={text ?? ''} readOnly={readOnly || uploading || text === null} invalid={tooLarge} onChange={changeText} describedBy="script-content-help script-content-size" />
            <p id="script-content-size" className="text-xs text-muted-foreground">
              {scriptSizeLabel(bytes)}. Do not put secrets in scripts: everyone with access to this page can read them.
            </p>
          </div>

          {attempted && problems.length > 0 && (
            <ul role="alert" className="space-y-1">
              {problems.map((p) => <li key={p} className="text-sm text-destructive">{p}</li>)}
            </ul>
          )}
        </div>

        <div className="flex items-center justify-between gap-4 border-t border-border px-5 py-3">
          <div className="min-w-0 flex-1">{status}</div>
          <div className="flex shrink-0 gap-2">
            {readOnly ? (
              <Button variant="outline" onClick={onClose}>Close</Button>
            ) : (
              <>
                <Button variant="secondary" onClick={cancel}>Cancel</Button>
                {state.kind === 'failed'
                  ? <Button onClick={() => void apply()}><RotateCw />Retry</Button>
                  : <Button onClick={() => void apply()} disabled={uploading || text === null || tooLarge || fileProblem !== null}>Done</Button>}
              </>
            )}
          </div>
        </div>
      </div>
      {environmentOpen && <ScriptEnvironmentDialog onClose={() => setEnvironmentOpen(false)} />}
    </div>,
    document.body,
  );
}
