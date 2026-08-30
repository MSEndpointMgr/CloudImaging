import { useState, useEffect } from 'react';
import { Trash2, Pencil, Plus, ImageOff } from 'lucide-react';
import { useAuth } from '../context/authContext.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { useSort, sortRows } from '../lib/tableSort.ts';
import { Button } from '../components/ui/button.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { ChunkedUploadDialog } from '../components/ChunkedUploadDialog.tsx';
import { ImageEditorDialog } from '../components/ImageEditorDialog.tsx';

interface OsImage {
  imageId: string;
  name: string;
  version: string;
  description?: string;
  sizeBytes: number;
  sha256Hash: string;
  uploadedAt: string;
  isInUse: boolean;
}

type OsImageSortKey = 'name' | 'version' | 'size' | 'sha256' | 'uploaded' | 'status';

const OS_IMAGE_SORT_ACCESSORS: Record<OsImageSortKey, (row: OsImage) => string | number> = {
  name:     row => row.name,
  version:  row => row.version,
  size:     row => row.sizeBytes,
  sha256:   row => row.sha256Hash,
  uploaded: row => row.uploadedAt,
  status:   row => (row.isInUse ? 'In Use' : 'Available'),
};

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

// Per specs/001-cloud-windows-imaging/plan.md, the OS image catalog is sized for up to 500
// entries — a much larger ceiling than the Boot/Recovery media's 5-entry rotation, so it's
// enforced as a hard reject (see OsImageRepository.MaxActiveEntries) rather than an auto-demote.
const MAX_OS_IMAGES = 500;

/** OS Images management page (T087, FR-036, FR-037). */
export default function OsImagesPage(): React.ReactElement {
  const { isAdministrator } = useAuth();
  const [images, setImages]   = useState<OsImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [removing, setRemoving] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [editingImage, setEditingImage] = useState<OsImage | null>(null);
  const [sort, toggleSort] = useSort<OsImageSortKey>({ key: 'uploaded', dir: 'desc' });

  const loadImages = async () => {
    setLoading(true);
    try {
      const res = await apiFetchWithRetry('/api/images', { credentials: 'include' });
      if (res.ok) setImages(await res.json() as OsImage[]);
      else setImages([]);
    } catch { setImages([]); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  // Only images that are not in use can be removed / selected for removal.
  const removable = images.filter(img => !img.isInUse);
  const allSelected  = removable.length > 0 && removable.every(img => checked.has(img.imageId));
  const someSelected = removable.some(img => checked.has(img.imageId));
  const sortedImages = sortRows(images, sort, OS_IMAGE_SORT_ACCESSORS);

  const toggleRow   = (id: string) => setChecked(prev => { const n = new Set(prev); if (n.has(id)) { n.delete(id); } else { n.add(id); } return n; });
  const selectAll   = () => setChecked(new Set(removable.map(img => img.imageId)));
  const deselectAll = () => setChecked(new Set());

  const handleDelete = async (imageId: string) => {
    if (!confirm('Remove this OS image from the catalog? This cannot be undone.')) return;
    const res = await apiFetch(`/api/images/${imageId}`, { method: 'DELETE', credentials: 'include' });
    if (res.status === 409) { alert('Image is in use by an active session.'); return; }
    if (res.ok) { setChecked(prev => { const n = new Set(prev); n.delete(imageId); return n; }); void loadImages(); }
  };

  const handleRemoveSelected = async () => {
    const ids = [...checked];
    if (ids.length === 0) return;
    if (!confirm(`Remove ${ids.length} image${ids.length !== 1 ? 's' : ''} from the catalog? This cannot be undone.`)) return;
    setRemoving(true);
    try {
      await Promise.all(ids.map(id => apiFetch(`/api/images/${id}`, { method: 'DELETE', credentials: 'include' })));
    } finally {
      setRemoving(false);
      setChecked(new Set());
      void loadImages();
    }
  };

  const columnCount = isAdministrator ? 8 : 7;
  const selectedCount = checked.size;
  const atCapacity = images.length >= MAX_OS_IMAGES;

  return (
    <>
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <span className="text-xs text-muted-foreground">
          {images.length} / {MAX_OS_IMAGES} active images
        </span>
        {isAdministrator && (
          <Button onClick={() => setUploadOpen(true)} disabled={atCapacity} title={atCapacity ? 'Catalog is at capacity' : undefined}>
            <Plus size={14} /> Upload Image
          </Button>
        )}
      </div>

      {isAdministrator && atCapacity && (
        <p className="rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm text-amber-700 dark:text-amber-400">
          The OS image catalog is at capacity ({MAX_OS_IMAGES} images). Remove an unused image before uploading another.
        </p>
      )}

      {isAdministrator && selectedCount > 0 && (
        <div className="flex items-center justify-between rounded-lg border border-primary/30 bg-primary/10 px-4 py-2.5 text-sm">
          <span className="font-medium">{selectedCount} image{selectedCount !== 1 ? 's' : ''} selected</span>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => void handleRemoveSelected()}
            disabled={removing}
          >
            <Trash2 size={14} /> {removing ? 'Removing…' : 'Remove Selected'}
          </Button>
        </div>
      )}

      <div className="rounded-md border border-border overflow-hidden">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {isAdministrator && (
                <TableHead className="w-10">
                  <input
                    type="checkbox"
                    role="checkbox"
                    aria-label={allSelected ? 'Deselect all images' : 'Select all images'}
                    checked={allSelected}
                    ref={el => { if (el) el.indeterminate = someSelected && !allSelected; }}
                    onChange={() => (allSelected ? deselectAll() : selectAll())}
                    disabled={removable.length === 0}
                    className="h-4 w-4 rounded border-input align-middle accent-primary disabled:opacity-40"
                  />
                </TableHead>
              )}
              <SortableHead label="Name" sortKey="name" sort={sort} onSort={toggleSort} className="w-[30%]" />
              <SortableHead label="Version" sortKey="version" sort={sort} onSort={toggleSort} className="w-[13%]" />
              <SortableHead label="Size" sortKey="size" sort={sort} onSort={toggleSort} className="w-[9%]" />
              <SortableHead label="SHA-256" sortKey="sha256" sort={sort} onSort={toggleSort} className="w-[13%]" />
              <SortableHead label="Uploaded" sortKey="uploaded" sort={sort} onSort={toggleSort} className="w-[14%]" />
              <SortableHead label="Status" sortKey="status" sort={sort} onSort={toggleSort} className="w-[10%]" />
              <TableHead className="w-[90px]">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {loading ? (
              Array.from({ length: 5 }).map((_, i) => (
                <TableRow key={`skeleton-${i}`} className="hover:bg-transparent">
                  {Array.from({ length: columnCount }).map((__, j) => (
                    <TableCell key={j}><Skeleton className="h-4 w-full max-w-[8rem]" /></TableCell>
                  ))}
                </TableRow>
              ))
            ) : images.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columnCount} className="p-0">
                  <EmptyState
                    icon={ImageOff}
                    title="No images found in the catalog"
                    description="Upload an OS image to make it available for imaging."
                  />
                </TableCell>
              </TableRow>
            ) : sortedImages.map(img => (
              <TableRow
                key={img.imageId}
                data-state={checked.has(img.imageId) ? 'selected' : undefined}
              >
                {isAdministrator && (
                  <TableCell className="w-10">
                    <input
                      type="checkbox"
                      role="checkbox"
                      aria-label={`Select ${img.name}`}
                      checked={checked.has(img.imageId)}
                      onChange={() => toggleRow(img.imageId)}
                      disabled={img.isInUse}
                      title={img.isInUse ? 'Image is in use by an active session' : undefined}
                      className="h-4 w-4 rounded border-input align-middle accent-primary disabled:opacity-40"
                    />
                  </TableCell>
                )}
                <TableCell className="max-w-0 truncate font-medium" title={img.name}>{img.name}</TableCell>
                <TableCell className="truncate">{img.version}</TableCell>
                <TableCell className="truncate">{fmtSize(img.sizeBytes)}</TableCell>
                <TableCell className="truncate font-mono text-xs text-muted-foreground">{img.sha256Hash.slice(0, 12)}…</TableCell>
                <TableCell className="truncate text-xs text-muted-foreground">
                  {formatDateTime(img.uploadedAt)}
                </TableCell>
                <TableCell>
                  {img.isInUse ? (
                    <Badge variant="info" dot>In Use</Badge>
                  ) : (
                    <Badge variant="success" dot>Available</Badge>
                  )}
                </TableCell>
                <TableCell>
                  <div className="flex items-center gap-1">
                    {isAdministrator ? (
                      <>
                        <button onClick={() => setEditingImage(img)} className="rounded-md p-1.5 text-muted-foreground hover:bg-accent hover:text-primary" title="Edit">
                          <Pencil size={14} />
                        </button>
                        {!img.isInUse && (
                          <button onClick={() => void handleDelete(img.imageId)} className="rounded-md p-1.5 text-muted-foreground hover:bg-destructive/10 hover:text-destructive" title="Remove">
                            <Trash2 size={14} />
                          </button>
                        )}
                      </>
                    ) : (
                      <span className="text-xs text-muted-foreground">-</span>
                    )}
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>

      <ChunkedUploadDialog
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onUploaded={() => { setUploadOpen(false); void loadImages(); }}
        existingVersions={images.map(img => img.version)}
        atCapacity={atCapacity}
      />

      <ImageEditorDialog
        image={editingImage}
        onClose={() => setEditingImage(null)}
        onSaved={() => void loadImages()}
        existingVersions={images.filter(img => img.imageId !== editingImage?.imageId).map(img => img.version)}
      />
    </>
  );
}
