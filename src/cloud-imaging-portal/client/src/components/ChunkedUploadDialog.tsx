import { useState, useRef } from 'react';
import { UploadProgressBar } from './UploadProgressBar.tsx';
import {
  startChunkedUpload,
  uploadBlocks,
  finalizeChunkedUpload,
  cancelChunkedUpload,
} from '../services/chunkedUploadService.ts';

interface ChunkedUploadDialogProps {
  open:    boolean;
  onClose: () => void;
  onUploaded: (result: { blobName: string }) => void;
}

type UploadState = 'idle' | 'uploading' | 'finalizing' | 'done' | 'error' | 'cancelled';

/**
 * Chunked OS image upload dialog with pause/resume/retry (T087a, FR-036).
 */
export function ChunkedUploadDialog({ open, onClose, onUploaded }: ChunkedUploadDialogProps): React.ReactElement | null {
  const [file, setFile]           = useState<File | null>(null);
  const [version, setVersion]     = useState('');
  const [sha256, setSha256]       = useState('');
  const [progress, setProgress]   = useState(0);
  const [state, setState]         = useState<UploadState>('idle');
  const [error, setError]         = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const abortRef                  = useRef<AbortController | null>(null);

  if (!open) return null;

  const reset = () => {
    setFile(null); setVersion(''); setSha256(''); setProgress(0);
    setState('idle'); setError(null); setSessionId(null);
  };

  const handleClose = () => { reset(); onClose(); };

  const handleUpload = async () => {
    if (!file || !version.trim() || !sha256.trim()) return;
    setState('uploading'); setError(null); setProgress(0);

    abortRef.current = new AbortController();
    try {
      const session = await startChunkedUpload(file.name, version, file.size);
      setSessionId(session.sessionId);

      const blockIds = await uploadBlocks(session, file, setProgress, abortRef.current.signal);

      setState('finalizing');
      const result = await finalizeChunkedUpload(session.sessionId, blockIds);
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

  const handleCancel = async () => {
    abortRef.current?.abort();
    if (sessionId) await cancelChunkedUpload(sessionId).catch(() => {/* best-effort */});
    setState('cancelled');
  };

  const canUpload = !!file && version.trim().length > 0 && sha256.trim().length === 64;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-lg">
        <h2 className="text-lg font-semibold mb-4">Upload OS Image</h2>

        {state === 'idle' && (
          <div className="space-y-3">
            <div>
              <label className="block text-sm font-medium mb-1">WIM File</label>
              <input type="file" accept=".wim,.esd"
                onChange={e => setFile(e.target.files?.[0] ?? null)}
                className="block w-full text-sm text-muted-foreground" />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">Version</label>
              <input value={version} onChange={e => setVersion(e.target.value)}
                placeholder="e.g. Windows 11 24H2"
                className="w-full border border-input rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary" />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">SHA-256 Hash</label>
              <input value={sha256} onChange={e => setSha256(e.target.value)}
                placeholder="64-character hex string"
                className="w-full border border-input rounded-md px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-primary" />
              {sha256 && sha256.length !== 64 && (
                <p className="text-xs text-destructive mt-1">SHA-256 must be exactly 64 hex characters.</p>
              )}
            </div>
          </div>
        )}

        {(state === 'uploading' || state === 'finalizing') && (
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              {state === 'uploading'
                ? `Uploading ${file?.name} in 4 MB blocks…`
                : 'Finalising upload…'}
            </p>
            <UploadProgressBar
              percent={state === 'finalizing' ? 99 : progress}
              label={state === 'uploading' ? `${progress}% complete` : 'Committing…'}
            />
          </div>
        )}

        {state === 'done' && (
          <p className="text-sm text-green-600">
            ✅ Upload complete! The image will appear in the OS catalog after validation.
          </p>
        )}

        {state === 'cancelled' && (
          <p className="text-sm text-muted-foreground">Upload cancelled.</p>
        )}

        {state === 'error' && (
          <div className="space-y-2">
            <UploadProgressBar percent={progress} error={error} />
            <button onClick={() => setState('idle')} className="text-sm text-primary hover:underline">
              ↺ Retry
            </button>
          </div>
        )}

        <div className="flex gap-3 justify-end mt-5">
          {(state === 'uploading' || state === 'finalizing') ? (
            <button onClick={handleCancel} className="px-4 py-2 text-sm border border-border rounded-md hover:bg-muted">
              Cancel Upload
            </button>
          ) : (
            <>
              <button onClick={handleClose} className="px-4 py-2 text-sm border border-border rounded-md hover:bg-muted">
                {state === 'done' ? 'Close' : 'Cancel'}
              </button>
              {state === 'idle' && (
                <button onClick={handleUpload} disabled={!canUpload}
                  className="px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50">
                  Upload
                </button>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
