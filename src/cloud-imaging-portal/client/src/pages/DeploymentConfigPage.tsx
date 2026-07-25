import { useState, useEffect } from 'react';
import { PreFlightAuthorizationToggle } from '../components/PreFlightAuthorizationToggle.tsx';
import { BootMediaCertPanel } from '../components/BootMediaCertPanel.tsx';
import { apiFetch } from '../lib/apiClient.ts';
import { cn } from '../lib/utils';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';

interface PortalConfig {
  devicePreFlightAuthorizationEnabled: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
}

interface CertMeta {
  thumbprintDisplay?: string;
  issuedAt?: string;
  expiresAt?: string;
  isActive?: boolean;
}

type TabKey = 'security' | 'preflight' | 'misc';

const TABS: { key: TabKey; label: string }[] = [
  { key: 'security',  label: 'Security' },
  { key: 'preflight', label: 'Preflight' },
  { key: 'misc',      label: 'Miscellaneous' },
];

/**
 * Deployment configuration page.
 * Groups the deployment settings into tabs: Security (certificate & token
 * validation plus boot media certificate management), Preflight, and
 * Miscellaneous.
 */
export default function DeploymentConfigPage(): React.ReactElement {
  const [activeTab, setActiveTab] = useState<TabKey>('security');
  const [config, setConfig] = useState<PortalConfig>({
    devicePreFlightAuthorizationEnabled: false,
    sasTokenUrlExpiryMinutes:  60,
    bootImageSasExpiryMinutes: 60,
    certValidityPeriodDays:    365,
    clockSkewToleranceSeconds: 30,
  });
  const [certMeta, setCertMeta] = useState<CertMeta | null>(null);
  const [loading, setLoading]   = useState(true);
  const [saving, setSaving]     = useState(false);
  const [saved, setSaved]       = useState(false);
  const [error, setError]       = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const [cfgRes, certRes] = await Promise.all([
        apiFetch('/api/portal-config', { credentials: 'include' }),
        apiFetch('/api/cert/active',   { credentials: 'include' }),
      ]);
      if (cfgRes.ok)  setConfig(await cfgRes.json() as PortalConfig);
      if (certRes.ok) setCertMeta(await certRes.json() as CertMeta);
    } catch { /* use defaults */ }
    finally { setLoading(false); }
  };

  useEffect(() => { void load(); }, []);

  const save = async () => {
    setSaving(true); setError(null); setSaved(false);
    try {
      const res = await apiFetch('/api/portal-config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(config),
      });
      if (res.ok || res.status === 204) { setSaved(true); setTimeout(() => setSaved(false), 3000); }
      else setError('Failed to save configuration.');
    } catch { setError('Network error.'); }
    finally { setSaving(false); }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  const numField = (
    key: keyof PortalConfig,
    min: number,
    max: number,
    label: string,
    description: string,
  ) => (
    <div>
      <label className="block text-sm font-medium mb-1">{label}</label>
      <p className="mb-1.5 text-xs text-muted-foreground">{description}</p>
      <Input type="number" min={min} max={max}
        value={config[key] as number}
        onChange={e => setConfig(c => ({ ...c, [key]: Number(e.target.value) }))}
        className="w-40" />
    </div>
  );

  return (
    <div className="max-w-2xl space-y-6">
      {/* Tab bar */}
      <div className="border-b border-border">
        <nav className="flex flex-wrap gap-1" role="tablist" aria-label="Configuration areas">
          {TABS.map(t => (
            <button
              key={t.key}
              type="button"
              role="tab"
              aria-selected={activeTab === t.key}
              onClick={() => setActiveTab(t.key)}
              className={cn(
                '-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                activeTab === t.key
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {t.label}
            </button>
          ))}
        </nav>
      </div>

      {/* Security */}
      {activeTab === 'security' && (
        <div className="space-y-6">
          <div className="rounded-md border border-border p-4 space-y-5">
            <div>
              <h3 className="text-sm font-semibold">Certificate &amp; Token Validation</h3>
              <p className="mt-1 text-xs text-muted-foreground">
                Controls how long issued certificates stay valid and how much clock
                difference is tolerated when validating tokens and certificates.
              </p>
            </div>
            <div className="grid grid-cols-2 gap-6">
              {numField('certValidityPeriodDays', 30, 3650, 'Certificate Validity Period (days)',
                'The lifetime of newly issued boot media certificates before they expire and must be rotated.')}
              {numField('clockSkewToleranceSeconds', 0, 300, 'Clock Skew Tolerance (seconds)',
                'The permitted time difference between a device clock and the server clock when validating tokens and certificates.')}
            </div>
          </div>
          <BootMediaCertPanel certMeta={certMeta} onCertChanged={() => void load()} />
        </div>
      )}

      {/* Preflight */}
      {activeTab === 'preflight' && (
        <PreFlightAuthorizationToggle
          enabled={config.devicePreFlightAuthorizationEnabled}
          onChange={v => setConfig(c => ({ ...c, devicePreFlightAuthorizationEnabled: v }))}
        />
      )}

      {/* Miscellaneous */}
      {activeTab === 'misc' && (
        <div className="rounded-md border border-border p-4 space-y-5">
          <div>
            <h3 className="text-sm font-semibold">Download Link Expiry</h3>
            <p className="mt-1 text-xs text-muted-foreground">
              Controls how long the temporary download links generated for image
              downloads remain usable.
            </p>
          </div>
          <div className="grid grid-cols-2 gap-6">
            {numField('sasTokenUrlExpiryMinutes', 15, 1440, 'OS Image Download Link Expiry (minutes)',
              'How long a generated download link for an operating system image stays valid before it must be regenerated.')}
            {numField('bootImageSasExpiryMinutes', 15, 1440, 'Boot Image Download Link Expiry (minutes)',
              'How long a generated download link for a boot image stays valid before it must be regenerated.')}
          </div>
        </div>
      )}

      {/* Save controls apply to all settings tabs; the boot media certificate
          panel within the Security tab has its own separate actions. */}
      <div className="space-y-2">
        {error && <p className="text-sm text-destructive">{error}</p>}
        {saved && <p className="text-sm text-green-600">Configuration saved.</p>}
        <Button onClick={save} disabled={saving}>
          {saving ? 'Saving…' : 'Save Configuration'}
        </Button>
      </div>
    </div>
  );
}
