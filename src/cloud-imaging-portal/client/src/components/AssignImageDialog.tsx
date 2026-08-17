import { useState, useEffect } from 'react';
import { apiFetch } from '../lib/apiClient.ts';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';

interface OsImage {
  imageId: string;
  name: string;
  version: string;
  sizeBytes: number;
  isActive: boolean;
}

interface AssignImageDialogProps {
  open: boolean;
  sessionId: string | null;
  onClose: () => void;
  onAssigned: (sessionId: string, imageId: string) => void;
}

/**
 * OS image selection modal used for both single-session and bulk assignment (T042, FR-033).
 */
export function AssignImageDialog({ open, sessionId, onClose, onAssigned }: AssignImageDialogProps): React.ReactElement | null {
  const [images, setImages] = useState<OsImage[]>([]);
  const [search, setSearch] = useState('');
  const [selectedImageId, setSelectedImageId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    void (async () => {
      try {
        const res = await apiFetch('/api/images', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as OsImage[];
          setImages(data.filter(i => i.isActive));
        }
      } catch {
        // Images failed to load. Show empty list
      }
    })();
  }, [open]);

  if (!open || !sessionId) return null;

  const filtered = images.filter(
    i => i.name.toLowerCase().includes(search.toLowerCase())
      || i.version.toLowerCase().includes(search.toLowerCase())
  );

  const handleAssign = async () => {
    if (!selectedImageId) return;
    setBusy(true);
    setError(null);
    try {
      const res = await apiFetch(`/api/sessions/${sessionId}/assign`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ osImageId: selectedImageId }),
      });
      if (res.status === 201) {
        onAssigned(sessionId, selectedImageId);
        onClose();
        return;
      }
      setError(res.status === 409
        ? 'This session cannot be assigned (wrong state).'
        : 'Assignment failed. Please try again.');
    } catch {
      setError('Network error. Please try again.');
    } finally {
      setBusy(false);
    }
  };

  const handleClose = () => {
    setSelectedImageId(null);
    setSearch('');
    setError(null);
    onClose();
  };

  const fmt = (bytes: number) => `${(bytes / 1_073_741_824).toFixed(1)} GB`;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-lg">
        <h2 className="text-lg font-semibold mb-4">Select OS Image</h2>
        <Input
          type="search"
          placeholder="Search images…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="mb-3"
        />
        <div className="max-h-60 overflow-y-auto border border-border rounded-md mb-4">
          {filtered.length === 0 ? (
            <p className="text-sm text-muted-foreground p-4 text-center">No images found.</p>
          ) : (
            filtered.map(img => (
              <button
                key={img.imageId}
                onClick={() => setSelectedImageId(img.imageId)}
                className={[
                  'w-full flex items-center gap-3 px-3 py-2.5 text-left text-sm border-b border-border last:border-0 hover:bg-muted/50 transition-colors',
                  selectedImageId === img.imageId ? 'bg-primary/10' : '',
                ].join(' ')}
              >
                <span className={[
                  'w-4 h-4 rounded-full border-2 flex-shrink-0 transition-colors',
                  selectedImageId === img.imageId ? 'border-primary bg-primary' : 'border-muted-foreground',
                ].join(' ')} />
                <span className="flex-1">
                  <span className="font-medium">{img.name}</span>
                  <span className="text-muted-foreground ml-2">{img.version}</span>
                </span>
                <span className="text-muted-foreground">{fmt(img.sizeBytes)}</span>
              </button>
            ))
          )}
        </div>
        {error && <p className="text-sm text-destructive mb-3">{error}</p>}
        <div className="flex gap-3 justify-end">
          <Button variant="outline" onClick={handleClose} disabled={busy}>
            Cancel
          </Button>
          <Button onClick={handleAssign} disabled={busy || !selectedImageId}>
            {busy ? 'Assigning…' : 'Assign Image'}
          </Button>
        </div>
      </div>
    </div>
  );
}
