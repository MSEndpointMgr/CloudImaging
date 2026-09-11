import { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { apiFetch } from '../lib/apiClient.ts';
import { isDuplicateVersion } from '../lib/versionSuggestion.ts';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';

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
  /** Versions of other images in the catalog; the edited version must not match any of these. */
  existingVersions?: string[];
}

/**
 * Modal for editing OS image metadata (T088, FR-037).
 * Sends PATCH /api/images/{imageId} with updated name, version, description.
 */
export function ImageEditorDialog({ image, onClose, onSaved, existingVersions = [] }: ImageEditorDialogProps): React.ReactElement | null {
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

  const duplicateVersion = isDuplicateVersion(version, existingVersions);

  const handleSave = async () => {
    if (duplicateVersion) {
      setError(`Version "${version.trim()}" already exists.`);
      return;
    }
    setSaving(true); setError(null);
    try {
      const res = await apiFetch(`/api/images/${image.imageId}`, {
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

  // Portalled to document.body so the dimming overlay always covers the full viewport (including
  // the app header) regardless of any stacking context introduced by this dialog's call site.
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-md">
        <h2 className="text-lg font-semibold mb-4">Edit Image</h2>

        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium mb-1">Name</label>
            <Input value={name} onChange={e => setName(e.target.value)} />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Version</label>
            <Input value={version} aria-invalid={duplicateVersion} onChange={e => setVersion(e.target.value)} />
            {duplicateVersion && (
              <p className="text-sm text-destructive mt-1">Version "{version.trim()}" already exists.</p>
            )}
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Description</label>
            <textarea value={description} onChange={e => setDesc(e.target.value)} rows={3}
              className="flex w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-50" />
          </div>
        </div>

        {error && <p className="text-sm text-destructive mt-3">{error}</p>}

        <div className="flex gap-3 justify-end mt-4">
          <Button variant="outline" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving || !name.trim() || duplicateVersion}>
            {saving ? 'Saving…' : 'Save Changes'}
          </Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
