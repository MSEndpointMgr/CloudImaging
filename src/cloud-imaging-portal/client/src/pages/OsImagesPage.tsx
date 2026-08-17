import { useState, useEffect } from 'react';
import { Trash2, Pencil, Plus } from 'lucide-react';
import { useAuth } from '../context/authContext.tsx';
import { apiFetch } from '../lib/apiClient.ts';
import { Button } from '../components/ui/button.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';
import { ChunkedUploadDialog } from '../components/ChunkedUploadDialog.tsx';

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

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

/** OS Images management page (T087, FR-036, FR-037). */
export default function OsImagesPage(): React.ReactElement {
  const { isAdministrator } = useAuth();
  const [images, setImages]   = useState<OsImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [removing, setRemoving] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);

  const loadImages = async () => {
    setLoading(true);
    try {
      const res = await apiFetch('/api/images', { credentials: 'include' });
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

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        {isAdministrator && (
          <Button onClick={() => setUploadOpen(true)}>
            <Plus size={14} /> Upload Image
          </Button>
        )}
      </div>

      <ChunkedUploadDialog
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onUploaded={() => { setUploadOpen(false); void loadImages(); }}
      />

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
        <Table>
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
              <TableHead>Name</TableHead>
              <TableHead>Version</TableHead>
              <TableHead>Size</TableHead>
              <TableHead>SHA-256</TableHead>
              <TableHead>Uploaded</TableHead>
              <TableHead>Status</TableHead>
              <TableHead>Actions</TableHead>
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
                <TableCell colSpan={columnCount} className="py-8 text-center text-muted-foreground">No images found in the catalog.</TableCell>
              </TableRow>
            ) : images.map(img => (
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
                <TableCell className="font-medium">{img.name}</TableCell>
                <TableCell>{img.version}</TableCell>
                <TableCell>{fmtSize(img.sizeBytes)}</TableCell>
                <TableCell className="font-mono text-xs text-muted-foreground">{img.sha256Hash.slice(0, 12)}…</TableCell>
                <TableCell className="text-xs text-muted-foreground">
                  {new Date(img.uploadedAt).toLocaleDateString()}
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
                        <button className="rounded-md p-1.5 text-muted-foreground hover:bg-accent hover:text-primary" title="Edit">
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
  );
}
