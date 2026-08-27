import { useState, useRef } from 'react';
import { X } from 'lucide-react';
import { UploadProgressBar } from './UploadProgressBar.tsx';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { Card, CardContent } from './ui/card.tsx';
import { IMAGE_FILE_ACCEPT, validateImageFile } from '../lib/imageFileValidation.ts';
import { computeSha256Streaming } from '../lib/sha256.ts';
import { isDuplicateVersion } from '../lib/versionSuggestion.ts';
import {
  startChunkedUpload,
  uploadBlocks,
  finalizeChunkedUpload,
} from '../services/chunkedUploadService.ts';

interface ChunkedUploadDialogProps {
  open:    boolean;
  onClose: () => void;
  onUploaded: (result: { blobName: string }) => void;
  /** Versions already present in the catalog; the new version must not match any of these. */
  existingVersions?: string[];
}

type UploadState = 'idle' | 'hashing' | 'uploading' | 'finalizing' | 'done' | 'error' | 'cancelled';

export function ChunkedUploadDialog({ open, onClose, onUploaded, existingVersions = [] }: ChunkedUploadDialogProps): React.ReactElement | null {
  const [file, setFile]           = useState<File | null>(null);
  const [version, setVersion]     = useState('');
  const [sha256, setSha256]       = useState('');
  // Tracks the checksum computation, which starts as soon as a file is chosen (so it's usually
  // already done by the time the operator finishes typing the version) rather than being
  // something the operator has to know/enter manually.
  const [hashProgress, setHashProgress] = useState(0);
  const [progress, setProgress]   = useState(0);
  const [state, setState]         = useState<UploadState>('idle');
  const [error, setError]         = useState<string | null>(null);
  const abortRef                  = useRef<AbortController | null>(null);
  const hashRunId                 = useRef(0);
  const fileInputRef              = useRef<HTMLInputElement>(null);

  if (!open) return null;

  const busy = state === 'hashing' || state === 'uploading' || state === 'finalizing';

  const reset = () => {
    hashRunId.current++; // invalidate any in-flight hashing so it doesn't clobber state after reset
    setFile(null); setVersion(''); setSha256(''); setHashProgress(0); setProgress(0);
    setState('idle'); setError(null);
  };

  const handleClose = () => { reset(); onClose(); };

  const handleFileSelected = (selected: File | null) => {
    hashRunId.current++;
    const runId = hashRunId.current;
    setFile(selected);
    setSha256('');
    setHashProgress(0);

    if (!selected) {
      setState('idle');
      return;
    }

    setState('hashing');
    computeSha256Streaming(selected, percent => {
      if (hashRunId.current === runId) setHashProgress(percent);
    })
      .then(hash => {
        if (hashRunId.current !== runId) return; // superseded by a newer file selection / reset
        setSha256(hash);
        setState('idle');
      })
      .catch((err: unknown) => {
        if (hashRunId.current !== runId) return;
        setError(err instanceof Error ? err.message : 'Failed to compute file checksum.');
        setState('error');
      });
  };

  const handleUpload = async () => {
    if (!file || !version.trim() || sha256.length !== 64 || duplicateVersion) return;
    setState('uploading'); setError(null); setProgress(0);

    abortRef.current = new AbortController();
    try {
      const session = await startChunkedUpload(file.name, version, sha256);

      const blockIds = await uploadBlocks(session, file, setProgress, abortRef.current.signal);

      setState('finalizing');
      const result = await finalizeChunkedUpload(session, blockIds, file.name, version, sha256, file.size);
      setState('done');
      onUploaded(result as { blobName: string });
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        setState('cancelled');
      } else {
        setState('error');
        setError(err instanceof Error ? err.message : 'Unknown upload error.');
      }
    }
  };

  const handleCancel = () => {
    abortRef.current?.abort();
    setState('cancelled');
  };

  const stageLabel =
    state === 'hashing'    ? 'Computing checksum…'
    : state === 'uploading'  ? 'Uploading to storage…'
    : state === 'finalizing' ? 'Validating & publishing…'
    : '';
  const duplicateVersion = isDuplicateVersion(version, existingVersions);
  const canUpload = !!file && version.trim().length > 0 && sha256.length === 64 && state !== 'hashing' && !duplicateVersion;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <Card className="w-full max-w-lg">
        <CardContent className="space-y-4 py-6">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">Upload OS image</h2>
            <Button variant="ghost" size="icon" onClick={handleClose} disabled={busy} title="Close">
              <X className="h-4 w-4" />
            </Button>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="osImageVersion">Version</Label>
            <Input
              id="osImageVersion"
              placeholder="e.g. Windows 11 24H2"
              value={version}
              disabled={state === 'uploading' || state === 'finalizing'}
              aria-invalid={duplicateVersion}
              onChange={e => setVersion(e.target.value)}
            />
            {duplicateVersion && (
              <p className="text-xs text-destructive">Version "{version.trim()}" already exists.</p>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="osImageFile">Image file (.wim or .iso)</Label>
            <input
              ref={fileInputRef}
              id="osImageFile"
              type="file"
              accept={IMAGE_FILE_ACCEPT}
              className="hidden"
              disabled={state === 'uploading' || state === 'finalizing'}
              onChange={e => {
                const selected = e.target.files?.[0] ?? null;
                if (selected) {
                  const validationError = validateImageFile(selected);
                  if (validationError) {
                    setError(validationError);
                    e.target.value = '';
                    handleFileSelected(null);
                    return;
                  }
                }
                setError(null);
                handleFileSelected(selected);
              }}
            />
            <div className="flex items-center gap-3">
              <Button
                type="button"
                variant="outline"
                disabled={state === 'uploading' || state === 'finalizing'}
                onClick={() => fileInputRef.current?.click()}
              >
                Choose file
              </Button>
              <span className="truncate text-sm text-muted-foreground">
                {file ? file.name : 'No file selected'}
              </span>
            </div>
            {/* The checksum is computed automatically from the selected file — the operator
                never has to know or type it in. */}
            {state === 'hashing' && (
              <UploadProgressBar percent={hashProgress} label="Computing checksum…" />
            )}
            {state !== 'hashing' && sha256 && (
              <p className="truncate font-mono text-xs text-muted-foreground" title={sha256}>
                SHA-256: {sha256}
              </p>
            )}
          </div>

          {(state === 'uploading' || state === 'finalizing') && (
            <UploadProgressBar
              percent={state === 'finalizing' ? 100 : progress}
              label={stageLabel}
            />
          )}

          {state === 'error' && (
            <div className="space-y-1">
              <p className="text-sm text-destructive">{error}</p>
              <button onClick={() => setState('idle')} className="text-sm text-primary hover:underline">
                Retry
              </button>
            </div>
          )}

          {state === 'cancelled' && (
            <p className="text-sm text-muted-foreground">Upload cancelled.</p>
          )}

          <div className="flex justify-end gap-2 pt-2">
            {state === 'uploading' || state === 'finalizing' ? (
              <Button variant="outline" onClick={handleCancel}>Cancel upload</Button>
            ) : (
              <>
                <Button variant="outline" onClick={handleClose}>
                  {state === 'done' ? 'Close' : 'Cancel'}
                </Button>
                {state !== 'done' && (
                  <Button onClick={() => void handleUpload()} disabled={!canUpload}>
                    {state === 'hashing' ? 'Computing checksum…' : 'Upload & publish'}
                  </Button>
                )}
              </>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

