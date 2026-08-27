import { useState, useEffect, useRef } from 'react';
import { Trash2, Upload, X, ShieldCheck } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Input } from '../components/ui/input';
import { Label } from '../components/ui/label';
import { Badge } from '../components/ui/badge';
import { EmptyState } from '../components/ui/empty-state';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table';
import { UploadProgressBar } from '../components/UploadProgressBar.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { IMAGE_FILE_ACCEPT, validateImageFile } from '../lib/imageFileValidation.ts';
import { formatDateTime } from '../lib/utils.ts';
import {
  startRecoveryImageUpload,
  uploadRecoveryFileToBlobStorage,
  publishRecoveryImageUpload,
} from '../services/recoveryImageUploadService.ts';

interface RecoveryImage {
  recoveryImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  sha256Hash: string;
  isLatestPublished: boolean;
  isActive: boolean;
}

/** Maximum number of active recovery image entries (mirrors the boot image catalog cap). */
const MAX_RECOVERY_IMAGES = 5;

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

/**
 * Recovery Images management page: WinRE images applied to the Recovery partition during
 * imaging. Devices always fetch whichever entry is currently published, the same
 * "isLatestPublished" convention used by the Boot Images catalog. Administrator manages entries.
 */
export default function RecoveryImagesPage(): React.ReactElement {
  const { isAdministrator } = useAuth();
  const { notify } = useToast();
  const [images, setImages]   = useState<RecoveryImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploadOpen, setUploadOpen] = useState(false);

  const loadImages = async () => {
    setLoading(true);
    try {
      const res = await apiFetchWithRetry('/api/recovery-images', { credentials: 'include' });
      if (res.ok) setImages(await res.json() as RecoveryImage[]);
      else notify({ status: 'error', title: 'Failed to load recovery images.' });
    } catch { notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' }); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  const handleDelete = async (id: string) => {
    if (!confirm('Delete this recovery image?')) return;
    const res = await apiFetch(`/api/recovery-images/${id}`, { method: 'DELETE', credentials: 'include' });
    if (res.ok || res.status === 204) { void loadImages(); return; }
    if (res.status === 409) {
      const msg = await res.text().catch(() => null);
      notify({ status: 'error', title: msg || 'Cannot delete the currently published recovery image. Publish a replacement first.' });
    }
  };

  const used      = images.length;
  const remaining = Math.max(0, MAX_RECOVERY_IMAGES - used);
  const atCapacity = remaining === 0;
  const usedPct   = Math.min(100, Math.round((used / MAX_RECOVERY_IMAGES) * 100));

  return (
    <>
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm text-muted-foreground">
            Windows Recovery Environment (WinRE) images applied to the Recovery partition during imaging.
          </p>
        </div>
        {isAdministrator && (
          <Button onClick={() => setUploadOpen(true)}>
            <Upload className="h-4 w-4" />
            Upload recovery image
          </Button>
        )}
      </div>

      {/* Capacity indicator */}
      <Card>
        <CardContent className="flex flex-col gap-4 py-5 sm:flex-row sm:items-center">
          <div className="flex items-center gap-3 sm:w-44">
            <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <ShieldCheck className="h-5 w-5" aria-hidden="true" />
            </div>
            <div>
              <p className="text-sm text-muted-foreground">Active entries</p>
              <p className="text-2xl font-semibold tabular-nums">
                {used}<span className="text-base font-normal text-muted-foreground"> / {MAX_RECOVERY_IMAGES}</span>
              </p>
            </div>
          </div>
          <div className="flex-1">
            <div className="mb-1.5 flex items-center justify-between text-xs">
              <span className={atCapacity ? 'font-medium text-amber-600 dark:text-amber-400' : 'text-muted-foreground'}>
                {atCapacity
                  ? 'At capacity. The oldest entry is replaced on the next upload'
                  : `${remaining} slot${remaining === 1 ? '' : 's'} remaining`}
              </span>
              <span className="tabular-nums text-muted-foreground">{usedPct}%</span>
            </div>
            <div className="h-2 w-full overflow-hidden rounded-full bg-muted">
              <div
                className={['h-full rounded-full transition-all', atCapacity ? 'bg-amber-500' : 'bg-primary'].join(' ')}
                style={{ width: `${usedPct}%` }}
              />
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="rounded-md border border-border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead>Version</TableHead>
              <TableHead>Size</TableHead>
              <TableHead>SHA-256</TableHead>
              <TableHead>Created</TableHead>
              <TableHead>Status</TableHead>
              {isAdministrator && <TableHead>Actions</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableRow className="hover:bg-transparent"><TableCell colSpan={isAdministrator ? 6 : 5} className="py-10 text-center text-muted-foreground">Loading…</TableCell></TableRow>
            ) : images.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={isAdministrator ? 6 : 5} className="p-0">
                  <EmptyState
                    icon={ShieldCheck}
                    title="No recovery images"
                    description="Publish a recovery image from the Media Builder app to get started."
                  />
                </TableCell>
              </TableRow>
            ) : images.map(img => (
              <TableRow key={img.recoveryImageId}>
                <TableCell className="font-medium">{img.version}</TableCell>
                <TableCell>{fmtSize(img.sizeBytes)}</TableCell>
                <TableCell className="font-mono text-xs text-muted-foreground">{img.sha256Hash.slice(0, 12)}…</TableCell>
                <TableCell className="text-xs text-muted-foreground">{formatDateTime(img.createdAt)}</TableCell>
                <TableCell>
                  {img.isLatestPublished
                    ? <Badge variant="info" dot>Latest</Badge>
                    : <Badge variant="muted" dot>Active</Badge>}
                </TableCell>
                {isAdministrator && (
                  <TableCell>
                    <Button
                      variant="ghost"
                      size="icon"
                      title={img.isLatestPublished ? 'Publish a replacement before deleting' : 'Delete'}
                      disabled={img.isLatestPublished}
                      className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                      onClick={() => void handleDelete(img.recoveryImageId)}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>

      {uploadOpen && (
        <UploadRecoveryImageDialog
          atCapacity={atCapacity}
          onClose={() => setUploadOpen(false)}
          onPublished={() => { setUploadOpen(false); void loadImages(); }}
        />
      )}
    </>
  );
}

type UploadStage = 'form' | 'hashing' | 'uploading' | 'publishing';

interface UploadRecoveryImageDialogProps {
  atCapacity: boolean;
  onClose: () => void;
  onPublished: () => void;
}

/** Staged recovery image upload modal: hash → SAS upload → publish. */
function UploadRecoveryImageDialog({ atCapacity, onClose, onPublished }: UploadRecoveryImageDialogProps): React.ReactElement {
  const [version, setVersion] = useState('');
  const [file, setFile]       = useState<File | null>(null);
  const [stage, setStage]     = useState<UploadStage>('form');
  const [percent, setPercent] = useState(0);
  const [error, setError]     = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const busy = stage !== 'form';

  const handleSubmit = async () => {
    if (!version.trim() || !file) {
      setError('Provide a version and select a .wim or .iso file.');
      return;
    }
    setError(null);
    try {
      setStage('hashing');
      const sha256Hash = await computeSha256(file);

      const session = await startRecoveryImageUpload(version.trim(), sha256Hash, file.name);

      setStage('uploading');
      setPercent(0);
      await uploadRecoveryFileToBlobStorage(session.uploadUrl, file, setPercent);

      setStage('publishing');
      await publishRecoveryImageUpload({ ...session, sha256Hash }, file.size, version.trim());

      onPublished();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed.');
      setStage('form');
    }
  };

  const stageLabel =
    stage === 'hashing'    ? 'Computing checksum…'
    : stage === 'uploading'  ? 'Uploading to storage…'
    : stage === 'publishing' ? 'Validating & publishing…'
    : '';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <Card className="w-full max-w-lg">
        <CardContent className="space-y-4 py-6">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">Upload recovery image</h2>
            <Button variant="ghost" size="icon" onClick={onClose} disabled={busy} title="Close">
              <X className="h-4 w-4" />
            </Button>
          </div>

          {atCapacity && (
            <p className="rounded-md bg-amber-500/10 px-3 py-2 text-xs text-amber-600 dark:text-amber-400">
              The catalog is at capacity ({MAX_RECOVERY_IMAGES}). Publishing will replace the oldest entry.
            </p>
          )}

          <div className="space-y-1.5">
            <Label htmlFor="recoveryImageVersion">Version</Label>
            <Input
              id="recoveryImageVersion"
              placeholder="e.g. 2026.07.1"
              value={version}
              disabled={busy}
              onChange={e => setVersion(e.target.value)}
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="recoveryImageFile">Recovery media (.wim or .iso)</Label>
            <input
              ref={fileInputRef}
              id="recoveryImageFile"
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
                {file ? `${file.name} · ${fmtSize(file.size)}` : 'No file selected'}
              </span>
            </div>
          </div>

          {busy && <UploadProgressBar percent={stage === 'uploading' ? percent : 100} label={stageLabel} />}
          {error && <p className="text-sm text-destructive">{error}</p>}

          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button onClick={() => void handleSubmit()} disabled={busy || !version.trim() || !file}>
              {busy ? 'Working…' : 'Upload & publish'}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

/** Computes the SHA-256 hash of a file and returns it as a lowercase hex string. */
async function computeSha256(file: File): Promise<string> {
  const buffer = await file.arrayBuffer();
  const digest = await crypto.subtle.digest('SHA-256', buffer);
  return Array.from(new Uint8Array(digest))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
}
