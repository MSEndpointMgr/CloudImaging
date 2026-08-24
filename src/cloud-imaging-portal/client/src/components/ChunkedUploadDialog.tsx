import { useState, useRef } from 'react';
import { X } from 'lucide-react';
import { UploadProgressBar } from './UploadProgressBar.tsx';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { Card, CardContent } from './ui/card.tsx';
import { IMAGE_FILE_ACCEPT, validateImageFile } from '../lib/imageFileValidation.ts';
import {
  startChunkedUpload,
  uploadBlocks,
  finalizeChunkedUpload,
} from '../services/chunkedUploadService.ts';

interface ChunkedUploadDialogProps {
  open:    boolean;
  onClose: () => void;
  onUploaded: (result: { blobName: string }) => void;
}

type UploadState = 'idle' | 'uploading' | 'finalizing' | 'done' | 'error' | 'cancelled';

export function ChunkedUploadDialog({ open, onClose, onUploaded }: ChunkedUploadDialogProps): React.ReactElement | null {
  const [file, setFile]           = useState<File | null>(null);
  const [version, setVersion]     = useState('');
  const [sha256, setSha256]       = useState('');
  const [progress, setProgress]   = useState(0);
  const [state, setState]         = useState<UploadState>('idle');
  const [error, setError]         = useState<string | null>(null);
  const abortRef                  = useRef<AbortController | null>(null);
  const fileInputRef              = useRef<HTMLInputElement>(null);

  if (!open) return null;

  const busy = state === 'uploading' || state === 'finalizing';

  const reset = () => {
    setFile(null); setVersion(''); setSha256(''); setProgress(0);
    setState('idle'); setError(null);
  };

  const handleClose = () => { reset(); onClose(); };

  const handleUpload = async () => {
    if (!file || !version.trim() || sha256.trim().length !== 64) return;
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
    state === 'uploading'  ? 'Uploading to storage…'
    : state === 'finalizing' ? 'Validating & publishing…'
    : '';

  const canUpload = !!file && version.trim().length > 0 && sha256.trim().length === 64;

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
              disabled={busy}
              onChange={e => setVersion(e.target.value)}
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="osImageFile">Image file (.wim or .iso)</Label>
            <input
              ref={fileInputRef}
              id="osImageFile"
              type="file"
              accept={IMAGE_FILE_ACCEPT}
              className="hidden"
              disabled={busy}
              onChange={e => {
                const selected = e.target.files?.[0] ?? null;
                if (selected) {
                  const validationError = validateImageFile(selected);
                  if (validationError) {
                    setError(validationError);
                    setFile(null);
                    e.target.value = '';
                    return;
                  }
                }
                setFile(selected);
                setError(null);
              }}
            />
            <div className="flex items-center gap-3">
              <Button type="button" variant="outline" disabled={busy} onClick={() => fileInputRef.current?.click()}>
                Choose file
              </Button>
              <span className="truncate text-sm text-muted-foreground">
                {file ? file.name : 'No file selected'}
              </span>
            </div>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="osImageSha256">SHA-256 hash</Label>
            <Input
              id="osImageSha256"
              value={sha256}
              disabled={busy}
              onChange={e => setSha256(e.target.value)}
              placeholder="64-character hex string"
              className="font-mono"
            />
            {sha256 && sha256.length !== 64 && (
              <p className="text-xs text-destructive">Must be exactly 64 hex characters.</p>
            )}
          </div>

          {busy && (
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
            {busy ? (
              <Button variant="outline" onClick={handleCancel}>Cancel upload</Button>
            ) : (
              <>
                <Button variant="outline" onClick={handleClose}>
                  {state === 'done' ? 'Close' : 'Cancel'}
                </Button>
                {state !== 'done' && (
                  <Button onClick={() => void handleUpload()} disabled={!canUpload}>
                    Upload & publish
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
