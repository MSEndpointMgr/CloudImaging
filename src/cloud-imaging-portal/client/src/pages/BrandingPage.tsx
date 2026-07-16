import { useState, useEffect } from 'react';

interface BrandingConfig {
  logoBlobPath?: string;
  primaryColor: string;
  accentColor: string;
  applicationName: string;
}

/** Branding settings page (T095, FR-038). Administrator-only writes. */
export default function BrandingPage(): React.ReactElement {
  const [config, setConfig] = useState<BrandingConfig>({
    primaryColor: '#0078d4', accentColor: '#005a9e', applicationName: 'Cloud Imaging',
  });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving]   = useState(false);
  const [saved, setSaved]     = useState(false);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        const res = await fetch('/api/branding', { credentials: 'include' });
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
      const res = await fetch('/api/branding', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(config),
      });
      if (res.ok || res.status === 204) {
        setSaved(true);
        setTimeout(() => setSaved(false), 3000);
      } else {
        setError('Failed to save branding settings.');
      }
    } catch { setError('Network error.'); }
    finally { setSaving(false); }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  return (
    <div className="max-w-xl space-y-6">
      <h1 className="text-xl font-semibold">Branding Settings</h1>

      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium mb-1">Application Name</label>
          <input
            type="text"
            value={config.applicationName}
            onChange={e => setConfig({ ...config, applicationName: e.target.value })}
            className="w-full border border-input rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
          />
        </div>

        <div className="flex gap-4">
          <div className="flex-1">
            <label className="block text-sm font-medium mb-1">Primary Color</label>
            <div className="flex items-center gap-2">
              <input type="color" value={config.primaryColor}
                onChange={e => setConfig({ ...config, primaryColor: e.target.value })}
                className="w-10 h-10 rounded cursor-pointer border border-input" />
              <input type="text" value={config.primaryColor}
                onChange={e => setConfig({ ...config, primaryColor: e.target.value })}
                className="flex-1 border border-input rounded-md px-3 py-2 text-sm font-mono" />
            </div>
          </div>
          <div className="flex-1">
            <label className="block text-sm font-medium mb-1">Accent Color</label>
            <div className="flex items-center gap-2">
              <input type="color" value={config.accentColor}
                onChange={e => setConfig({ ...config, accentColor: e.target.value })}
                className="w-10 h-10 rounded cursor-pointer border border-input" />
              <input type="text" value={config.accentColor}
                onChange={e => setConfig({ ...config, accentColor: e.target.value })}
                className="flex-1 border border-input rounded-md px-3 py-2 text-sm font-mono" />
            </div>
          </div>
        </div>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}
      {saved && <p className="text-sm text-green-600">Branding settings saved.</p>}

      <button
        onClick={handleSave}
        disabled={saving}
        className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
      >
        {saving ? 'Saving…' : 'Save Branding'}
      </button>
    </div>
  );
}
