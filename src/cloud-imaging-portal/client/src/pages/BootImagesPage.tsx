import { useState, useEffect, useRef } from 'react';
import { Trash2, Upload, X, HardDrive } from 'lucide-react';
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
import { computeSha256Streaming } from '../lib/sha256.ts';
import { formatDateTime } from '../lib/utils.ts';
import { suggestVersionFromFileName, isDuplicateVersion } from '../lib/versionSuggestion.ts';
import {
  startBootImageUpload,
  uploadFileToBlobStorage,
  publishBootImageUpload,
} from '../services/bootImageUploadService.ts';

interface BootImage {
  bootImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  sha256Hash: string;
  isLatestPublished: boolean;
  isActive: boolean;
}

/** Maximum number of active boot image entries (FR-063). */
const MAX_BOOT_IMAGES = 5;

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

/** Boot Images management page (T127, US7, FR-063). Administrator manages entries. */
export default function BootImagesPage(): React.ReactElement {
  const { isAdministrator } = useAuth();
  const { notify } = useToast();
  const [images, setImages]   = useState<BootImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploadOpen, setUploadOpen] = useState(false);

  const loadImages = async () => {
    setLoading(true);
    try {
      const res = await apiFetchWithRetry('/api/boot-images', { credentials: 'include' });
      if (res.ok) {
        const data = await res.json() as BootImage[];
        setImages(data.sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
      } else notify({ status: 'error', title: 'Failed to load boot images.' });
    } catch { notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' }); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  const handleDelete = async (id: string) => {
    if (!confirm('Delete this boot image?')) return;
    const res = await apiFetch(`/api/boot-images/${id}`, { method: 'DELETE', credentials: 'include' });
    if (res.ok || res.status === 204) void loadImages();
  };

  const used      = images.length;
  const remaining = Math.max(0, MAX_BOOT_IMAGES - used);
  const atCapacity = remaining === 0;
  const usedPct   = Math.min(100, Math.round((used / MAX_BOOT_IMAGES) * 100));

  return (
    <>
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        {isAdministrator && (
          <Button onClick={() => setUploadOpen(true)}>
            <Upload className="h-4 w-4" />
            Upload boot image
          </Button>
        )}
      </div>

      {/* Capacity indicator (FR-063) */}
      <Card>
        <CardContent className="flex flex-col gap-4 py-5 sm:flex-row sm:items-center">
          <div className="flex items-center gap-3 sm:w-44">
            <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <HardDrive className="h-5 w-5" aria-hidden="true" />
            </div>
            <div>
              <p className="text-sm text-muted-foreground">Active entries</p>
              <p className="text-2xl font-semibold tabular-nums">
                {used}<span className="text-base font-normal text-muted-foreground"> / {MAX_BOOT_IMAGES}</span>
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
                    icon={HardDrive}
                    title="No boot images"
                    description="Publish a boot image from the Media Builder app to get started."
                  />
                </TableCell>
              </TableRow>
            ) : images.map(img => (
              <TableRow key={img.bootImageId}>
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
                      title="Delete"
                      className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                      onClick={() => void handleDelete(img.bootImageId)}
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
        <UploadBootImageDialog
          atCapacity={atCapacity}
          existingVersions={images.map(img => img.version)}
          onClose={() => setUploadOpen(false)}
          onPublished={() => { setUploadOpen(false); void loadImages(); }}
        />
      )}
    </>
  );
}

type UploadStage = 'form' | 'hashing' | 'uploading' | 'publishing';

interface UploadBootImageDialogProps {
  atCapacity: boolean;
  /** Versions already present in the catalog; the new version must not match any of these. */
  existingVersions: string[];
  onClose: () => void;
  onPublished: () => void;
}

/** Staged boot image upload modal: hash → SAS upload → publish (T128, FR-063). */
function UploadBootImageDialog({ atCapacity, existingVersions, onClose, onPublished }: UploadBootImageDialogProps): React.ReactElement {
  const [version, setVersion] = useState('');
  // Tracks whether the current `version` value was populated automatically from the
  // selected file's name, so a subsequent file pick can safely replace it — but a
  // manual edit to the field immediately "claims" it and stops any further auto-fill.
  const [versionAutoFilled, setVersionAutoFilled] = useState(false);
  const [file, setFile]       = useState<File | null>(null);
  const [stage, setStage]     = useState<UploadStage>('form');
  const [percent, setPercent] = useState(0);
  const [error, setError]     = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const busy = stage !== 'form';
  const trimmedVersion = version.trim();
  const duplicateVersion = isDuplicateVersion(trimmedVersion, existingVersions);

  const handleSubmit = async () => {
    if (!version.trim() || !file) {
      setError('Provide a version and select a .wim or .iso file.');
      return;
    }
    if (duplicateVersion) {
      setError(`Version "${trimmedVersion}" already exists. Choose a different version.`);
      return;
    }
    setError(null);
    try {
      setStage('hashing');
      setPercent(0);
      const sha256Hash = await computeSha256Streaming(file, setPercent);

      const session = await startBootImageUpload(version.trim(), sha256Hash, file.name);

      setStage('uploading');
      setPercent(0);
      await uploadFileToBlobStorage(session.uploadUrl, file, setPercent);

      setStage('publishing');
      await publishBootImageUpload({ ...session, sha256Hash }, file.size, version.trim());

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
            <h2 className="text-lg font-semibold">Upload boot image</h2>
            <Button variant="ghost" size="icon" onClick={onClose} disabled={busy} title="Close">
              <X className="h-4 w-4" />
            </Button>
          </div>

          {atCapacity && (
            <p className="rounded-md bg-amber-500/10 px-3 py-2 text-xs text-amber-600 dark:text-amber-400">
              The catalog is at capacity ({MAX_BOOT_IMAGES}). Publishing will replace the oldest entry.
            </p>
          )}

          <div className="space-y-1.5">
            <Label htmlFor="bootImageVersion">Version</Label>
            <Input
              id="bootImageVersion"
              placeholder="e.g. 2026.07.1"
              value={version}
              disabled={busy}
              aria-invalid={duplicateVersion}
              onChange={e => { setVersion(e.target.value); setVersionAutoFilled(false); }}
            />
            {duplicateVersion && (
              <p className="text-xs text-destructive">Version "{trimmedVersion}" already exists.</p>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="bootImageFile">Boot media (.wim or .iso)</Label>
            <input
              ref={fileInputRef}
              id="bootImageFile"
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
                // Auto-fill the version from a date embedded in the filename (e.g. the
                // `cloud-imaging-boot-20260827-170846.wim` Media Builder produces), unless
                // the operator has already typed their own version for this dialog session.
                if (selected && (version.trim() === '' || versionAutoFilled)) {
                  const suggested = suggestVersionFromFileName(selected.name, existingVersions);
                  if (suggested) {
                    setVersion(suggested);
                    setVersionAutoFilled(true);
                  }
                }
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

          {busy && <UploadProgressBar percent={stage === 'publishing' ? 100 : percent} label={stageLabel} />}
          {error && <p className="text-sm text-destructive">{error}</p>}

          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button onClick={() => void handleSubmit()} disabled={busy || !version.trim() || !file || duplicateVersion}>
              {busy ? 'Working…' : 'Upload & publish'}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

