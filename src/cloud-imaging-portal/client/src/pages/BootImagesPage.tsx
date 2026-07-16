import { useState, useEffect } from 'react';
import { Trash2 } from 'lucide-react';

interface BootImage {
  bootImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  sha256Hash: string;
  isLatestPublished: boolean;
  isActive: boolean;
}

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

/** Boot Images management page (T127, US7, FR-063). Administrator manages entries. */
export default function BootImagesPage(): React.ReactElement {
  const [images, setImages]   = useState<BootImage[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  const loadImages = async () => {
    setLoading(true); setError(null);
    try {
      const res = await fetch('/api/boot-images', { credentials: 'include' });
      if (res.ok) setImages(await res.json() as BootImage[]);
      else setError('Failed to load boot images.');
    } catch { setError('Network error.'); }
    finally { setLoading(false); }
  };

  useEffect(() => { void loadImages(); }, []);

  const handleDelete = async (id: string) => {
    if (!confirm('Delete this boot image?')) return;
    const res = await fetch(`/api/boot-images/${id}`, { method: 'DELETE', credentials: 'include' });
    if (res.ok || res.status === 204) void loadImages();
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Boot Images</h1>
        <p className="text-xs text-muted-foreground">Up to 5 active entries (FR-063)</p>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      <div className="rounded-md border border-border overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-muted/50">
            <tr>
              <th className="px-3 py-2 text-left font-medium">Version</th>
              <th className="px-3 py-2 text-left font-medium">Size</th>
              <th className="px-3 py-2 text-left font-medium">SHA-256</th>
              <th className="px-3 py-2 text-left font-medium">Created</th>
              <th className="px-3 py-2 text-left font-medium">Status</th>
              <th className="px-3 py-2 text-left font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading ? (
              <tr><td colSpan={6} className="py-8 text-center text-muted-foreground">Loading…</td></tr>
            ) : images.length === 0 ? (
              <tr><td colSpan={6} className="py-8 text-center text-muted-foreground">No boot images.</td></tr>
            ) : images.map(img => (
              <tr key={img.bootImageId} className="border-t border-border hover:bg-muted/30">
                <td className="px-3 py-2 font-medium">{img.version}</td>
                <td className="px-3 py-2">{fmtSize(img.sizeBytes)}</td>
                <td className="px-3 py-2 font-mono text-xs text-muted-foreground">{img.sha256Hash.slice(0, 12)}…</td>
                <td className="px-3 py-2 text-xs text-muted-foreground">
                  {new Date(img.createdAt).toLocaleDateString()}
                </td>
                <td className="px-3 py-2">
                  {img.isLatestPublished ? (
                    <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800">Latest</span>
                  ) : (
                    <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-medium bg-muted text-muted-foreground">Active</span>
                  )}
                </td>
                <td className="px-3 py-2">
                  <button onClick={() => void handleDelete(img.bootImageId)} className="p-1 hover:text-destructive" title="Delete">
                    <Trash2 size={14} />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
