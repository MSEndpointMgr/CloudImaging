import { useState, useEffect } from 'react';

interface OsImage {
  imageId: string;
  name: string;
  version: string;
  description?: string;
}

interface ImageEditorDialogProps {
  image: OsImage | null;
  onClose: () => void;
  onSaved: () => void;
}

/**
 * Modal for editing OS image metadata (T088, FR-037).
 * Sends PATCH /api/images/{imageId} with updated name, version, description.
 */
export function ImageEditorDialog({ image, onClose, onSaved }: ImageEditorDialogProps): React.ReactElement | null {
  const [name, setName]           = useState('');
  const [version, setVersion]     = useState('');
  const [description, setDesc]    = useState('');
  const [saving, setSaving]       = useState(false);
  const [error, setError]         = useState<string | null>(null);

  useEffect(() => {
    if (image) {
      setName(image.name);
      setVersion(image.version);
      setDesc(image.description ?? '');
      setError(null);
    }
  }, [image]);

  if (!image) return null;

  const handleSave = async () => {
    setSaving(true); setError(null);
    try {
      const res = await fetch(`/api/images/${image.imageId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ name, version, description }),
      });
      if (res.ok) { onSaved(); onClose(); }
      else setError('Failed to save changes.');
    } catch { setError('Network error.'); }
    finally { setSaving(false); }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-md">
        <h2 className="text-lg font-semibold mb-4">Edit Image</h2>

        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium mb-1">Name</label>
            <input value={name} onChange={e => setName(e.target.value)}
              className="w-full border border-input rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary" />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Version</label>
            <input value={version} onChange={e => setVersion(e.target.value)}
              className="w-full border border-input rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary" />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Description</label>
            <textarea value={description} onChange={e => setDesc(e.target.value)} rows={3}
              className="w-full border border-input rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary" />
          </div>
        </div>

        {error && <p className="text-sm text-destructive mt-3">{error}</p>}

        <div className="flex gap-3 justify-end mt-4">
          <button onClick={onClose} disabled={saving} className="px-4 py-2 text-sm border border-border rounded-md hover:bg-muted">
            Cancel
          </button>
          <button onClick={handleSave} disabled={saving || !name.trim()}
            className="px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50">
            {saving ? 'Saving…' : 'Save Changes'}
          </button>
        </div>
      </div>
    </div>
  );
}
