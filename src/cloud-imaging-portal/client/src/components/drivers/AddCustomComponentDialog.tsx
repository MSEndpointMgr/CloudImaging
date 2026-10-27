import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Plus, Trash2, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Input } from '../ui/input.tsx';
import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import { uploadBlocks } from '../../services/chunkedUploadService.ts';
import {
  CUSTOM_ACTIONS, DriverFieldError, abandonCustomUpload, actionLabel, completeCustomUpload, createCustomComponent, fetchModelPage, formatSize, startCustomUpload,
  type DriverAction, type DriverCustomComponent, type DriverModelRow,
} from '../../lib/drivers.ts';
import { useDialogFocus } from './useDialogFocus.ts';

const NOTHING = '';

type Upload =
  | { state: 'none' }
  | { state: 'uploading'; fileName: string; percent: number }
  | { state: 'ready'; uploadId: string; fileName: string; sizeBytes: number; infCount: number }
  | { state: 'failed'; message: string };

/** The vendor components every chosen model has, by lineage key, for the Replaces list. */
async function sharedComponents(modelIds: string[]): Promise<{ lineageKey: string; name: string }[]> {
  const pages = await Promise.all(modelIds.map(id => fetchModelPage(id)));
  const [first, ...rest] = pages.map(p => p.components.filter(c => c.source !== 'Custom'));
  return (first ?? [])
    .filter(c => rest.every(list => list.some(o => o.lineageKey === c.lineageKey)))
    .map(c => ({ lineageKey: c.lineageKey, name: c.name }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * Add custom component: a driver the vendor has not published yet. The package is staged straight
 * to storage and checked by Core before the form can be saved; field errors from Core show under
 * their fields.
 */
export function AddCustomComponentDialog({ models, initialModelId, onClose, onAdded }: {
  /** Onboarded models that can be chosen. */
  models: DriverModelRow[];
  /** The model the dialog was opened from. */
  initialModelId?: string;
  onClose: () => void;
  onAdded: (custom: DriverCustomComponent) => void;
}): React.ReactElement {
  const dialogRef = useRef<HTMLDivElement>(null);
  const [upload, setUpload] = useState<Upload>({ state: 'none' });
  const uploadRef = useRef<Upload>(upload);
  const saved = useRef(false);
  const abort = useRef<AbortController | null>(null);
  const [name, setName] = useState('');
  const [version, setVersion] = useState('');
  const [action, setAction] = useState<DriverAction>('Offline');
  const [category, setCategory] = useState('');
  const [command, setCommand] = useState('');
  const [modelIds, setModelIds] = useState<string[]>(initialModelId ? [initialModelId] : []);
  const [replaces, setReplaces] = useState(NOTHING);
  const [shared, setShared] = useState<{ lineageKey: string; name: string }[]>([]);
  const [sharedFailed, setSharedFailed] = useState(false);
  const [sharedAttempt, setSharedAttempt] = useState(0);
  const fileInput = useRef<HTMLInputElement>(null);
  const [fields, setFields] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => { uploadRef.current = upload; }, [upload]);

  // A package that was uploaded but never added is deleted again.
  const close = (): void => {
    abort.current?.abort();
    const current = uploadRef.current;
    if (!saved.current && current.state === 'ready') void abandonCustomUpload(current.uploadId).catch(() => undefined);
    onClose();
  };
  useDialogFocus(dialogRef, close);

  useEffect(() => {
    let current = true;
    setSharedFailed(false);
    if (modelIds.length === 0) {
      setShared([]);
      return undefined;
    }
    sharedComponents(modelIds)
      .then(list => { if (current) setShared(list); })
      .catch(() => { if (current) { setShared([]); setSharedFailed(true); } });
    return () => { current = false; };
  }, [modelIds, sharedAttempt]);

  // A package that is replaced or removed before saving is deleted again.
  const discard = (): void => {
    const current = uploadRef.current;
    if (current.state === 'ready') void abandonCustomUpload(current.uploadId).catch(() => undefined);
  };

  const removeFile = (): void => {
    abort.current?.abort();
    discard();
    setUpload({ state: 'none' });
    if (fileInput.current) fileInput.current.value = '';
  };

  const choose = async (file: File | undefined) => {
    if (!file) return;
    abort.current?.abort();
    discard();
    const controller = new AbortController();
    abort.current = controller;
    setUpload({ state: 'uploading', fileName: file.name, percent: 0 });
    try {
      const started = await startCustomUpload(file.name, file.size);
      const blockIds = await uploadBlocks({ ...started, blobName: '', expiresAt: '' }, file, percent => { if (!controller.signal.aborted) setUpload({ state: 'uploading', fileName: file.name, percent }); }, controller.signal);
      const checked = await completeCustomUpload(started.uploadId, blockIds);
      // Another file was chosen, or the dialog closed, while this one finished.
      if (controller.signal.aborted) {
        void abandonCustomUpload(started.uploadId).catch(() => undefined);
        return;
      }
      const ready: Upload = { state: 'ready', uploadId: started.uploadId, fileName: file.name, sizeBytes: checked.sizeBytes, infCount: checked.infCount };
      uploadRef.current = ready;
      setUpload(ready);
    } catch (err) {
      if (!controller.signal.aborted) setUpload({ state: 'failed', message: err instanceof Error ? err.message : 'The package could not be uploaded.' });
    }
  };

  const specialize = action !== 'Offline';
  const save = async () => {
    setSaving(true);
    setError(null);
    setFields({});
    try {
      const added = await createCustomComponent({
        uploadId: upload.state === 'ready' ? upload.uploadId : undefined,
        name: name.trim(),
        version: version.trim(),
        action,
        category: category.trim(),
        installCommand: specialize ? command.trim() || null : null,
        targets: modelIds.map(id => ({ modelId: id, replacesLineageKey: replaces || null })),
      });
      saved.current = true;
      onAdded(added);
    } catch (err) {
      if (err instanceof DriverFieldError && Object.keys(err.fields).length > 0) setFields(err.fields);
      else setError(err instanceof Error ? err.message : 'Could not add the custom component.');
      setSaving(false);
    }
  };

  const fieldError = (field: string): React.ReactNode =>
    fields[field] ? <p id={`custom-${field}-error`} className="text-sm text-destructive">{fields[field]}</p> : null;

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="custom-dialog-title" className="flex max-h-full w-full max-w-lg flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="custom-dialog-title" className="text-base font-semibold">Add custom component</h2>
            <p className="text-sm text-muted-foreground">Use a driver the vendor has not published yet. It is checked and stored like a vendor component.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={close} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          <div className="space-y-2">
            <Label htmlFor="custom-package">Package</Label>
            <input ref={fileInput} id="custom-package" type="file" accept=".zip,.exe,.msi" className="block w-full text-sm" onChange={e => void choose(e.target.files?.[0])} aria-describedby="custom-package-help" />
            {upload.state === 'uploading' && (
              <div className="space-y-1">
                <div role="progressbar" aria-label={`Uploading ${upload.fileName}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={upload.percent} className="h-2 w-full overflow-hidden rounded-full bg-muted">
                  <div className="h-full bg-primary" style={{ width: `${upload.percent}%` }} />
                </div>
                <p className="text-sm text-muted-foreground">{`Uploading ${upload.fileName}, ${upload.percent}%`}</p>
              </div>
            )}
            {upload.state === 'ready' && (
              <div className="flex items-center justify-between gap-2">
                <p className="min-w-0 truncate text-sm">{`${upload.fileName}, ${formatSize(upload.sizeBytes)}${upload.fileName.toLowerCase().endsWith('.zip') ? `, ${upload.infCount === 1 ? '1 INF file found' : `${upload.infCount} INF files found`}` : ''}`}</p>
                <Button variant="ghost" size="sm" onClick={removeFile} disabled={saving}><Trash2 />Remove file</Button>
              </div>
            )}
            {upload.state === 'failed' && <p className="text-sm text-destructive">{upload.message}</p>}
            {fieldError('uploadId')}
            <p id="custom-package-help" className="text-sm text-muted-foreground">For Offline, a ZIP with the driver&apos;s INF files. For Specialize, an installer. Drivers must be signed; unsigned drivers fail on the device.</p>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="custom-name">Name</Label>
              <Input id="custom-name" value={name} maxLength={100} onChange={e => setName(e.target.value)} aria-invalid={!!fields['name']} aria-describedby={fields['name'] ? 'custom-name-error' : undefined} />
              {fieldError('name')}
            </div>
            <div className="space-y-2">
              <Label htmlFor="custom-version">Version</Label>
              <Input id="custom-version" value={version} maxLength={50} onChange={e => setVersion(e.target.value)} aria-invalid={!!fields['version']} aria-describedby={fields['version'] ? 'custom-version-error' : undefined} />
              {fieldError('version')}
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="custom-action">Action</Label>
              <Select id="custom-action" value={action} onValueChange={v => setAction(v as DriverAction)} options={CUSTOM_ACTIONS.map(a => ({ value: a, label: actionLabel(a) }))} aria-label="Action" />
              {fieldError('action')}
            </div>
            <div className="space-y-2">
              <Label htmlFor="custom-category">Category</Label>
              <Input id="custom-category" value={category} maxLength={100} onChange={e => setCategory(e.target.value)} aria-invalid={!!fields['category']} aria-describedby={fields['category'] ? 'custom-category-error' : undefined} />
              {fieldError('category')}
            </div>
          </div>

          {specialize && (
            <div className="space-y-2">
              <Label htmlFor="custom-command">Silent command</Label>
              <Input id="custom-command" value={command} placeholder="setup.exe /quiet /norestart" onChange={e => setCommand(e.target.value)} aria-invalid={!!fields['installCommand']} aria-describedby={fields['installCommand'] ? 'custom-installCommand-error' : undefined} />
              {fieldError('installCommand')}
            </div>
          )}

          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">Models</legend>
            <div className="max-h-40 space-y-1 overflow-y-auto rounded-md border border-border p-2">
              {models.map(model => (
                <label key={model.modelId} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={modelIds.includes(model.modelId)}
                    onChange={e => { setReplaces(NOTHING); setModelIds(ids => e.target.checked ? [...ids, model.modelId] : ids.filter(id => id !== model.modelId)); }}
                  />
                  {model.displayName}
                </label>
              ))}
            </div>
            {fieldError('targets')}
            <p className="text-sm text-muted-foreground">All Windows releases of each model. Offline drivers only install on hardware they match.</p>
          </fieldset>

          <div className="space-y-2">
            <Label htmlFor="custom-replaces">Replaces</Label>
            <Select
              id="custom-replaces"
              value={replaces}
              onValueChange={setReplaces}
              options={[{ value: NOTHING, label: 'Nothing, add alongside' }, ...shared.map(c => ({ value: c.lineageKey, label: c.name }))]}
              aria-label="Replaces"
              disabled={modelIds.length === 0}
            />
            {sharedFailed && (
              <p role="alert" className="flex items-center gap-2 text-sm text-destructive">
                The components of these models could not be loaded.
                <Button variant="link" className="h-auto px-0" onClick={() => setSharedAttempt(n => n + 1)}>Retry</Button>
              </p>
            )}
            <p className="text-sm text-muted-foreground">The replaced vendor component is skipped on these models until you remove this one.</p>
          </div>
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={close}>Cancel</Button>
          <Button onClick={() => void save()} loading={saving} disabled={upload.state !== 'ready'}><Plus />Add component</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
