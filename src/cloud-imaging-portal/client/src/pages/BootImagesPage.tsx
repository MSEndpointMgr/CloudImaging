import { useState, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { Trash2, Upload, X, HardDrive, Rocket, Undo2 } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Input } from '../components/ui/input';
import { Label } from '../components/ui/label';
import { Badge } from '../components/ui/badge';
import { EmptyState } from '../components/ui/empty-state';
import { TableSkeletonRows } from '../components/ui/skeleton';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { RelativeTime } from '../components/ui/relative-time.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { UploadProgressBar } from '../components/UploadProgressBar.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { fileAccept, WIM_ONLY_EXTENSIONS, validateImageFile } from '../lib/imageFileValidation.ts';
import { computeSha256Streaming } from '../lib/sha256.ts';
import { useSort, sortRows } from '../lib/tableSort.ts';
import { suggestVersionFromFileName, isDuplicateVersion } from '../lib/versionSuggestion.ts';
import { architectureLabel, countByArchitecture } from '../lib/wimMetadata.ts';
import { useWimArchitecture } from '../lib/useWimArchitecture.ts';
import { ArchitectureField } from '../components/ArchitectureField.tsx';
import { ArchitectureCapacityCard } from '../components/ArchitectureCapacityCard.tsx';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';
import { BOOT_IMAGE_STAGE_LABELS, bootImageStage, demoteFallback } from '../lib/bootImageStage.ts';
import type { BadgeProps } from '../components/ui/badge';
import {
  startBootImageUpload,
  uploadFileToBlobStorage,
  publishBootImageUpload,
  type BootImageArchitecture,
} from '../services/bootImageUploadService.ts';
import {
  uploadJobProgressPercent,
  uploadJobStageLabel,
  type UploadJob,
} from '../services/uploadJobService.ts';

interface BootImage {
  bootImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  sha256Hash: string;
  architecture: 'x64' | 'arm64';
  isLatestPublished: boolean;
  isProduction?: boolean;
  promotedAt?: string | null;
  isActive: boolean;
}

type BootImageSortKey = 'version' | 'size' | 'sha256' | 'created' | 'status' | 'architecture';

const BOOT_IMAGE_SORT_ACCESSORS: Record<BootImageSortKey, (row: BootImage) => string | number> = {
  version: row => row.version,
  size:    row => row.sizeBytes,
  sha256:  row => row.sha256Hash,
  created: row => row.createdAt,
  status:  row => BOOT_IMAGE_STAGE_LABELS[bootImageStage(row)],
  architecture: row => row.architecture,
};

const STAGE_BADGE: Record<ReturnType<typeof bootImageStage>, BadgeProps['variant']> = {
  latest: 'info',
  production: 'success',
  preProduction: 'warning',
};

/** Maximum number of active boot image entries per architecture (FR-063). */
const MAX_BOOT_IMAGES = 5;

const archLabel = architectureLabel;

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
  const [stageChange, setStageChange] = useState<{ action: 'promote' | 'demote'; image: BootImage } | null>(null);
  const [changingStage, setChangingStage] = useState(false);
  const [sort, toggleSort] = useSort<BootImageSortKey>({ key: 'created', dir: 'desc' });

  const loadImages = async () => {
    setLoading(true);
    try {
      const res = await apiFetchWithRetry('/api/boot-images', { credentials: 'include' });
      if (res.ok) {
        setImages(await res.json() as BootImage[]);
      } else {
        // An empty catalog is a 200 with [], so a failure here is always a real one. Relaying
        // the server's reason matters most for the 403 a deployment gets when the portal
        // backend's managed identity has no CloudImaging.PortalAccess role yet: without it the
        // page is indistinguishable from one that simply has no boot images.
        setImages([]);
        notify({
          status: 'error',
          title: 'Failed to load boot images.',
          description: await extractErrorDetail(res, `The server responded with status ${String(res.status)}.`),
        });
      }
    } catch { notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' }); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  const handleDelete = async (id: string) => {
    if (!confirm('Delete this boot image?')) return;
    const res = await apiFetch(`/api/boot-images/${id}`, { method: 'DELETE', credentials: 'include' });
    if (res.ok || res.status === 204) { void loadImages(); return; }
    // Anything else must be reported. Silently ignoring it (as this did) made a rejected delete
    // — most commonly the 409 for the currently published entry — look like the button was dead.
    notify({
      status: 'error',
      title: 'Failed to delete boot image.',
      description: await extractErrorDetail(res, `The server responded with status ${String(res.status)}.`),
    });
  };

  // Capacity and "latest" are tracked per architecture, so publishing ARM64 never displaces x64.
  const activeCounts = countByArchitecture(images);
  const sortedImages = sortRows(images, sort, BOOT_IMAGE_SORT_ACCESSORS);

  const handleStageChange = async () => {
    if (!stageChange) return;
    const { action, image } = stageChange;
    setChangingStage(true);
    try {
      const res = await apiFetch(`/api/boot-images/${image.bootImageId}/${action}`, { method: 'POST', credentials: 'include' });
      if (res.ok) {
        notify({
          status: 'success',
          title: action === 'promote'
            ? `v${image.version} is now the latest ${archLabel(image.architecture)} boot image.`
            : `v${image.version} is back in pre-production.`,
        });
        setStageChange(null);
        void loadImages();
        return;
      }
      notify({
        status: 'error',
        title: action === 'promote' ? 'Failed to promote boot image.' : 'Failed to demote boot image.',
        description: await extractErrorDetail(res, `The server responded with status ${String(res.status)}.`),
      });
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setChangingStage(false);
    }
  };

  const demoteCopy = (candidate: BootImage): ConfirmImpactCopy => {
    const arch = archLabel(candidate.architecture);
    const title = `Demote v${candidate.version} to pre-production?`;
    if (!candidate.isLatestPublished) {
      return {
        confirmTitle: title,
        impact: 'Technicians can no longer prepare USB devices with it. Only Administrators see it in Media Builder. USB devices are not affected.',
        confirmLabel: 'Demote',
        destructive: false,
      };
    }
    const fallback = demoteFallback(images, candidate);
    return fallback
      ? {
        confirmTitle: title,
        impact: `Technicians can no longer use it. v${fallback.version} becomes the latest ${arch} image again, and ${arch} USB devices that updated to v${candidate.version} go back to v${fallback.version} on their next boot.`,
        confirmLabel: 'Demote',
        destructive: true,
      }
      : {
        confirmTitle: title,
        impact: `No other production ${arch} boot image exists. Technicians will have no ${arch} image to prepare USB devices with, and ${arch} USB devices keep the image they have until another is promoted.`,
        confirmLabel: 'Demote',
        destructive: true,
      };
  };

  const promoteCopy = (candidate: BootImage): ConfirmImpactCopy => {
    const arch = archLabel(candidate.architecture);
    const current = images.find(img => img.isLatestPublished && img.architecture === candidate.architecture);
    const replaces = current ? ` It replaces v${current.version} as the latest ${arch} image.` : '';
    return bootImageStage(candidate) === 'preProduction'
      ? {
        confirmTitle: `Promote v${candidate.version} to production?`,
        impact: `Technicians can prepare USB devices with it, and ${arch} USB devices update to it on their next boot.${replaces} Promote only after testing it on a device.`,
        confirmLabel: 'Promote',
        destructive: false,
      }
      : {
        confirmTitle: `Make v${candidate.version} the latest ${arch} boot image?`,
        impact: `${arch} USB devices update to it on their next boot.${replaces}`,
        confirmLabel: 'Make latest',
        destructive: false,
      };
  };

  return (
    <>
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground">
          WinPE images written to USB boot media and ISO files by the Media Builder.
        </p>
        {isAdministrator && (
          <Button onClick={() => setUploadOpen(true)}>
            <Upload />
            Upload boot image
          </Button>
        )}
      </div>

      {/* Capacity indicator (FR-063) */}
      <ArchitectureCapacityCard counts={activeCounts} max={MAX_BOOT_IMAGES} />

      <div className="rounded-md border border-border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <SortableHead label="Version" sortKey="version" sort={sort} onSort={toggleSort} />
              <SortableHead label="Architecture" sortKey="architecture" sort={sort} onSort={toggleSort} />
              <SortableHead label="Size" sortKey="size" sort={sort} onSort={toggleSort} />
              <SortableHead label="SHA-256" sortKey="sha256" sort={sort} onSort={toggleSort} />
              <SortableHead label="Created" sortKey="created" sort={sort} onSort={toggleSort} />
              <SortableHead label="Status" sortKey="status" sort={sort} onSort={toggleSort} />
                {isAdministrator && <TableHead className="w-px whitespace-nowrap">Actions</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              <TableSkeletonRows columns={isAdministrator ? 7 : 6} />
            ) : images.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={isAdministrator ? 7 : 6} className="p-0">
                  <EmptyState
                    icon={HardDrive}
                    title="No boot images"
                    description="Generate a boot image in the Media Builder app, then upload the WIM file here. It starts in pre-production so you can test it before promoting it."
                    action={isAdministrator ? (
                      <Button onClick={() => setUploadOpen(true)}>
                        <Upload />
                        Upload boot image
                      </Button>
                    ) : undefined}
                  />
                </TableCell>
              </TableRow>
            ) : sortedImages.map(img => (
              <TableRow key={img.bootImageId}>
                <TableCell className="font-medium">{img.version}</TableCell>
                <TableCell>
                  <Badge variant="outline">{archLabel(img.architecture)}</Badge>
                </TableCell>
                <TableCell>{fmtSize(img.sizeBytes)}</TableCell>
                <TableCell>
                  <CopyableId
                    value={img.sha256Hash}
                    display={`${img.sha256Hash.slice(0, 12)}\u2026`}
                    label="SHA-256 digest"
                    className="font-mono text-xs text-muted-foreground"
                  />
                </TableCell>
                <TableCell className="text-xs text-muted-foreground"><RelativeTime value={img.createdAt} /></TableCell>
                <TableCell>
                  <Badge variant={STAGE_BADGE[bootImageStage(img)]} dot>{BOOT_IMAGE_STAGE_LABELS[bootImageStage(img)]}</Badge>
                </TableCell>
                {isAdministrator && (
                  <TableCell className="w-px whitespace-nowrap">
                    <div className="flex items-center gap-1">
                      {!img.isLatestPublished && (
                        <Tooltip content={bootImageStage(img) === 'preProduction' ? 'Promote to production' : `Make latest ${archLabel(img.architecture)} image`}>
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label={`Promote boot image ${img.version}`}
                            className="text-muted-foreground hover:text-primary"
                            onClick={() => setStageChange({ action: 'promote', image: img })}
                          >
                            <Rocket />
                          </Button>
                        </Tooltip>
                      )}
                      {bootImageStage(img) !== 'preProduction' && (
                        <Tooltip content="Demote to pre-production">
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label={`Demote boot image ${img.version}`}
                            className="text-muted-foreground hover:text-primary"
                            onClick={() => setStageChange({ action: 'demote', image: img })}
                          >
                            <Undo2 />
                          </Button>
                        </Tooltip>
                      )}
                      {/* The latest entry cannot be deleted (Imaging Core returns 409) until another is promoted. */}
                      <Tooltip content={img.isLatestPublished ? `Promote another ${archLabel(img.architecture)} boot image before deleting` : 'Delete'}>
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label="Delete boot image"
                          disabled={img.isLatestPublished}
                          className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                          onClick={() => void handleDelete(img.bootImageId)}
                        >
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
    </div>

      {uploadOpen && (
        <UploadBootImageDialog
          activeCounts={activeCounts}
          existingVersions={images.map(img => img.version)}
          onClose={() => setUploadOpen(false)}
          onPublished={() => {
            setUploadOpen(false);
            notify({
              status: 'success',
              title: 'Boot image uploaded in pre-production.',
              description: 'Prepare a USB device with it in Media Builder, test it, then promote it here.',
            });
            void loadImages();
          }}
        />
      )}

      {stageChange && (
        <ConfirmImpactDialog
          copy={stageChange.action === 'promote' ? promoteCopy(stageChange.image) : demoteCopy(stageChange.image)}
          busy={changingStage}
          onCancel={() => setStageChange(null)}
          onConfirm={() => void handleStageChange()}
          titleId="boot-image-stage-change-title"
        />
      )}
    </>
  );
}

type UploadStage = 'form' | 'hashing' | 'uploading' | 'publishing';

interface UploadBootImageDialogProps {
  /** Active catalog entries per architecture; capacity is enforced per architecture. */
  activeCounts: Record<BootImageArchitecture, number>;
  /** Versions already present in the catalog; the new version must not match any of these. */
  existingVersions: string[];
  onClose: () => void;
  onPublished: () => void;
}

/** Staged boot image upload modal: hash → SAS upload → publish (T128, FR-063). */
function UploadBootImageDialog({ activeCounts, existingVersions, onClose, onPublished }: UploadBootImageDialogProps): React.ReactElement {
  const [version, setVersion] = useState('');
  // Tracks whether the current `version` value was populated automatically from the
  // selected file's name, so a subsequent file pick can safely replace it — but a
  // manual edit to the field immediately "claims" it and stops any further auto-fill.
  const [versionAutoFilled, setVersionAutoFilled] = useState(false);
  const wimArchitecture = useWimArchitecture();
  const { architecture } = wimArchitecture;
  const [file, setFile]       = useState<File | null>(null);
  const [stage, setStage]     = useState<UploadStage>('form');
  const [percent, setPercent] = useState(0);
  const [error, setError]     = useState<string | null>(null);
  // Publish returns 202 Accepted and the verification/publish work runs in a background job,
  // so this tracks what that job reports.
  const [publishJob, setPublishJob] = useState<UploadJob | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const busy = stage !== 'form';
  const trimmedVersion = version.trim();
  const duplicateVersion = isDuplicateVersion(trimmedVersion, existingVersions);
  const atCapacity = architecture !== '' && activeCounts[architecture] >= MAX_BOOT_IMAGES;

  const handleFileSelected = async (selected: File | null) => {
    const unsupported = await wimArchitecture.inspect(selected);
    if (unsupported) {
      setFile(null);
      setError(unsupported);
    }
  };

  const handleSubmit = async () => {
    if (!version.trim() || !file || architecture === '') {
      setError('Provide a version, select a .wim file, and choose its architecture.');
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
      setPublishJob(null);
      await publishBootImageUpload(
        { ...session, sha256Hash }, file.size, version.trim(), architecture,
        { onStatus: setPublishJob },
      );

      onPublished();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed.');
      setStage('form');
    }
  };

  const stageLabel =
    stage === 'hashing'    ? 'Computing checksum…'
    : stage === 'uploading'  ? 'Uploading to storage…'
    : stage === 'publishing' ? uploadJobStageLabel(publishJob)
    : '';
  const stagePercent = stage === 'publishing' ? uploadJobProgressPercent(publishJob) : percent;

  // Portalled to document.body so the dimming overlay always covers the full viewport (including
  // the app header) regardless of any stacking context introduced by this page's own layout.
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <Card className="w-full max-w-lg">
        <CardContent className="space-y-4 py-6">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">Upload boot image</h2>
            <Button variant="ghost" size="icon" onClick={onClose} disabled={busy} aria-label="Close">
                <X />
            </Button>
          </div>

          {atCapacity && (
            <p className="rounded-md bg-amber-500/10 px-3 py-2 text-xs text-amber-600 dark:text-amber-400">
              The {archLabel(architecture)} catalog is at capacity ({MAX_BOOT_IMAGES}). Publishing will replace the oldest {archLabel(architecture)} entry.
            </p>
          )}

          <div className="space-y-2">
            <Label htmlFor="bootImageFile">Boot media (.wim)</Label>
            <input
              ref={fileInputRef}
              id="bootImageFile"
              type="file"
              accept={fileAccept(WIM_ONLY_EXTENSIONS)}
              className="hidden"
              disabled={busy}
              onChange={e => {
                const selected = e.target.files?.[0] ?? null;
                if (selected) {
                  const validationError = validateImageFile(selected, WIM_ONLY_EXTENSIONS);
                  if (validationError) {
                    setError(validationError);
                    setFile(null);
                    void wimArchitecture.inspect(null);
                    e.target.value = '';
                    return;
                  }
                }
                setFile(selected);
                setError(null);
                void handleFileSelected(selected);
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

          <div className="space-y-2">
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
              <p className="text-sm text-destructive">Version "{trimmedVersion}" already exists.</p>
            )}
          </div>

          <ArchitectureField id="bootImageArchitecture" state={wimArchitecture} hasFile={!!file} disabled={busy} />

          {busy && <UploadProgressBar percent={stagePercent} label={stageLabel} />}
          {error && <p className="text-sm text-destructive">{error}</p>}

          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button onClick={() => void handleSubmit()} disabled={busy || !version.trim() || !file || duplicateVersion || architecture === ''}>
              {busy ? 'Working…' : 'Upload & publish'}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>,
    document.body,
  );
}

