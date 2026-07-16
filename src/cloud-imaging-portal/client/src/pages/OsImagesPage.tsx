import { useState, useEffect } from 'react';
import { Trash2, Pencil, Plus } from 'lucide-react';

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
  const [images, setImages]   = useState<OsImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  const loadImages = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch('/api/images', { credentials: 'include' });
      if (res.ok) setImages(await res.json() as OsImage[]);
      else setError('Failed to load images.');
    } catch { setError('Network error.'); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  const handleDelete = async (imageId: string) => {
    if (!confirm('Delete this OS image? This cannot be undone.')) return;
    const res = await fetch(`/api/images/${imageId}`, { method: 'DELETE', credentials: 'include' });
    if (res.status === 409) { alert('Image is in use by an active session.'); return; }
    if (res.ok) void loadImages();
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">OS Images</h1>
        <button className="flex items-center gap-2 px-4 py-1.5 text-sm bg-primary text-primary-foreground rounded-md hover:bg-primary/90">
          <Plus size={14} /> Upload Image
        </button>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      <div className="rounded-md border border-border overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-muted/50">
            <tr>
              <th className="px-3 py-2 text-left font-medium">Name</th>
              <th className="px-3 py-2 text-left font-medium">Version</th>
              <th className="px-3 py-2 text-left font-medium">Size</th>
              <th className="px-3 py-2 text-left font-medium">SHA-256</th>
              <th className="px-3 py-2 text-left font-medium">Uploaded</th>
              <th className="px-3 py-2 text-left font-medium">Status</th>
              <th className="px-3 py-2 text-left font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr><td colSpan={7} className="py-8 text-center text-muted-foreground">Loading…</td></tr>
            ) : images.length === 0 ? (
              <tr><td colSpan={7} className="py-8 text-center text-muted-foreground">No images in catalog.</td></tr>
            ) : images.map(img => (
              <tr key={img.imageId} className="border-t border-border hover:bg-muted/30">
                <td className="px-3 py-2 font-medium">{img.name}</td>
                <td className="px-3 py-2">{img.version}</td>
                <td className="px-3 py-2">{fmtSize(img.sizeBytes)}</td>
                <td className="px-3 py-2 font-mono text-xs text-muted-foreground">{img.sha256Hash.slice(0, 12)}…</td>
                <td className="px-3 py-2 text-xs text-muted-foreground">
                  {new Date(img.uploadedAt).toLocaleDateString()}
                </td>
                <td className="px-3 py-2">
                  {img.isInUse ? (
                    <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800">In Use</span>
                  ) : (
                    <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-medium bg-green-100 text-green-800">Available</span>
                  )}
                </td>
                <td className="px-3 py-2">
                  <div className="flex items-center gap-2">
                    <button className="p-1 hover:text-primary" title="Edit">
                      <Pencil size={14} />
                    </button>
                    {!img.isInUse && (
                      <button onClick={() => void handleDelete(img.imageId)} className="p-1 hover:text-destructive" title="Delete">
                        <Trash2 size={14} />
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
