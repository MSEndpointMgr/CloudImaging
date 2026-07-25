import { useState, useEffect, useRef } from 'react';
import { Upload, ImageIcon } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../components/ui/card';
import { Input } from '../components/ui/input';
import { Label } from '../components/ui/label';
import { Button } from '../components/ui/button';
import { useBranding } from '../context/brandingContext.tsx';
import { apiFetch } from '../lib/apiClient.ts';

interface BrandingConfig {
  logoBlobPath?: string;
  primaryColor: string;
  accentColor: string;
  applicationName: string;
}

/** Maximum logo size accepted by the portal (keeps the request within the API body limit). */
const MAX_LOGO_BYTES = 512 * 1024;

/** Reads a problem-details `detail`/`title` from an error response, falling back to a default message. */
async function extractError(res: Response, fallback: string): Promise<string> {
  try {
    const body = await res.json() as { detail?: string; title?: string };
    return body.detail ?? body.title ?? fallback;
  } catch {
    return fallback;
  }
}

/** Branding settings page (T095, FR-038). Administrator-only writes. */
export default function BrandingPage(): React.ReactElement {
  const { logoUrl, refresh } = useBranding();
  const [config, setConfig] = useState<BrandingConfig>({
    primaryColor: '#0078d4', accentColor: '#005a9e', applicationName: 'Cloud Imaging',
  });
  const [loading, setLoading]     = useState(true);
  const [saving, setSaving]       = useState(false);
  const [saved, setSaved]         = useState(false);
  const [error, setError]         = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [logoError, setLogoError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    void (async () => {
      try {
        const res = await apiFetch('/api/branding', { credentials: 'include' });
        if (res.ok) setConfig(await res.json() as BrandingConfig);
      } catch { /* fallback to defaults */ }
      finally { setLoading(false); }
    })();
  }, []);

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      const res = await apiFetch('/api/branding', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(config),
      });
      if (res.ok || res.status === 204) {
        setSaved(true);
        await refresh();
        setTimeout(() => setSaved(false), 3000);
      } else {
        setError(await extractError(res, 'Failed to save branding settings.'));
      }
    } catch { setError('Network error.'); }
    finally { setSaving(false); }
  };

  const handleLogoSelected = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = ''; // allow re-selecting the same file later
    if (!file) return;

    setLogoError(null);
    if (!file.type.startsWith('image/')) {
      setLogoError('Please choose an image file.');
      return;
    }
    if (file.size > MAX_LOGO_BYTES) {
      setLogoError('Logo must be 512 KB or smaller.');
      return;
    }

    setUploading(true);
    try {
      const dataBase64 = await readFileAsBase64(file);
      const res = await apiFetch('/api/branding/logo', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ fileName: file.name, contentType: file.type, dataBase64 }),
      });
      if (res.ok) {
        const updated = await res.json() as BrandingConfig;
        setConfig(prev => ({ ...prev, logoBlobPath: updated.logoBlobPath }));
        await refresh();
      } else {
        setLogoError('Failed to upload the logo.');
      }
    } catch {
      setLogoError('Network error while uploading the logo.');
    } finally {
      setUploading(false);
    }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <p className="text-sm text-muted-foreground">
          Customise how the portal appears to operators.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Logo</CardTitle>
          <CardDescription>
            Shown in the portal sidebar and embedded in the Imaging Client boot media built by the Media Builder. PNG or SVG on a transparent background works best (max 512&nbsp;KB).
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center gap-4">
          <div className="flex h-16 w-16 shrink-0 items-center justify-center overflow-hidden rounded-lg border border-border bg-muted">
            {logoUrl ? (
              <img src={logoUrl} alt="Current portal logo" className="h-full w-full object-contain" />
            ) : (
              <ImageIcon className="h-6 w-6 text-muted-foreground" aria-hidden="true" />
            )}
          </div>
          <div className="space-y-1.5">
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*"
              className="hidden"
              onChange={handleLogoSelected}
            />
            <Button
              type="button"
              variant="outline"
              disabled={uploading}
              onClick={() => fileInputRef.current?.click()}
            >
              <Upload className="mr-2 h-4 w-4" />
              {uploading ? 'Uploading…' : logoUrl ? 'Replace logo' : 'Upload logo'}
            </Button>
            {logoError && <p className="text-sm text-destructive">{logoError}</p>}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Appearance</CardTitle>
          <CardDescription>Application name and theme colours.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-1.5">
            <Label htmlFor="applicationName">Application name</Label>
            <Input
              id="applicationName"
              type="text"
              value={config.applicationName}
              onChange={e => setConfig({ ...config, applicationName: e.target.value })}
            />
          </div>

          <div className="flex gap-4">
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="primaryColor">Primary colour</Label>
              <div className="flex items-center gap-2">
                <input
                  id="primaryColor"
                  type="color"
                  value={config.primaryColor}
                  onChange={e => setConfig({ ...config, primaryColor: e.target.value })}
                  className="h-9 w-10 shrink-0 cursor-pointer rounded-md border border-input bg-transparent"
                />
                <Input
                  type="text"
                  value={config.primaryColor}
                  onChange={e => setConfig({ ...config, primaryColor: e.target.value })}
                  className="font-mono"
                />
              </div>
            </div>
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="accentColor">Accent colour</Label>
              <div className="flex items-center gap-2">
                <input
                  id="accentColor"
                  type="color"
                  value={config.accentColor}
                  onChange={e => setConfig({ ...config, accentColor: e.target.value })}
                  className="h-9 w-10 shrink-0 cursor-pointer rounded-md border border-input bg-transparent"
                />
                <Input
                  type="text"
                  value={config.accentColor}
                  onChange={e => setConfig({ ...config, accentColor: e.target.value })}
                  className="font-mono"
                />
              </div>
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="flex items-center gap-3">
        <Button onClick={handleSave} disabled={saving}>
          {saving ? 'Saving…' : 'Save branding'}
        </Button>
        {error && <p className="text-sm text-destructive">{error}</p>}
        {saved && <p className="text-sm text-emerald-600 dark:text-emerald-400">Branding settings saved.</p>}
      </div>
    </div>
  );
}

/** Reads a file and returns its base64 payload (without the data-URI prefix). */
function readFileAsBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      const comma = result.indexOf(',');
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'));
    reader.readAsDataURL(file);
  });
}

