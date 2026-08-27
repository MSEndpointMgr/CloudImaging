import { useState, useEffect, useRef, useCallback } from 'react';
import { Upload, ImageIcon, RotateCcw } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../components/ui/card';
import { Input } from '../components/ui/input';
import { Label } from '../components/ui/label';
import { Button, type ButtonStatus } from '../components/ui/button';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';

interface BrandingConfig {
  /** Boot image logo (embedded into boot media by the Media Builder). */
  logoBlobPath?: string;
  /** Portal header/sidebar logo (streamed to the browser). */
  portalLogoBlobPath?: string;
  primaryColor: string;
  accentColor: string;
  applicationName: string;
}

/** The subset of branding fields the "Save branding" button actually persists. */
interface AppearanceFields {
  primaryColor: string;
  accentColor: string;
  applicationName: string;
}

function appearanceOf(config: BrandingConfig): AppearanceFields {
  return {
    primaryColor: config.primaryColor,
    accentColor: config.accentColor,
    applicationName: config.applicationName,
  };
}

function appearanceEquals(a: AppearanceFields, b: AppearanceFields): boolean {
  return a.primaryColor === b.primaryColor
    && a.accentColor === b.accentColor
    && a.applicationName === b.applicationName;
}

/** Which logo an upload targets. */
type LogoKind = 'portal' | 'boot';

/** Maximum logo size accepted by the portal (keeps the request within the API body limit). */
const MAX_LOGO_BYTES = 512 * 1024;

/** Matches the server-side BrandingFunctions validation: a 6-digit hex color. */
const HEX_COLOR_PATTERN = /^#[0-9a-fA-F]{6}$/;

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
  const { notify, update } = useToast();
  const [config, setConfig] = useState<BrandingConfig>({
    primaryColor: '#0078d4', accentColor: '#005a9e', applicationName: 'Cloud Imaging',
  });
  /** Snapshot of the appearance fields as last loaded/saved. Used to detect unsaved changes. */
  const [savedAppearance, setSavedAppearance] = useState<AppearanceFields>({
    primaryColor: '#0078d4', accentColor: '#005a9e', applicationName: 'Cloud Imaging',
  });
  const isDirty = !appearanceEquals(appearanceOf(config), savedAppearance);
  const primaryColorError = HEX_COLOR_PATTERN.test(config.primaryColor) ? null : 'Must be a 6-digit hex color, e.g. #0078D4.';
  const accentColorError = HEX_COLOR_PATTERN.test(config.accentColor) ? null : 'Must be a 6-digit hex color, e.g. #005A9E.';
  const applicationNameError = config.applicationName.trim().length < 1 || config.applicationName.trim().length > 100
    ? 'Must be between 1 and 100 characters.'
    : null;
  const hasValidationError = !!(primaryColorError || accentColorError || applicationNameError);
  const [loading, setLoading]         = useState(true);
  const [saveStatus, setSaveStatus]   = useState<ButtonStatus>('idle');
  const [uploadingKind, setUploadingKind] = useState<LogoKind | null>(null);
  const [resettingKind, setResettingKind] = useState<LogoKind | null>(null);
  const [pendingReset, setPendingReset] = useState<LogoKind | null>(null);
  const [bootLogoUrl, setBootLogoUrl] = useState<string | null>(null);
  const bootLogoUrlRef = useRef<string | null>(null);
  const portalInputRef = useRef<HTMLInputElement>(null);
  const bootInputRef    = useRef<HTMLInputElement>(null);

  /** Streams the boot image logo bytes into an object URL for preview, revoking the previous one. */
  const loadBootPreview = useCallback(async () => {
    try {
      const res = await apiFetchWithRetry('/api/branding/logo/content', { credentials: 'include' });
      const next = res.ok ? URL.createObjectURL(await res.blob()) : null;
      if (bootLogoUrlRef.current) URL.revokeObjectURL(bootLogoUrlRef.current);
      bootLogoUrlRef.current = next;
      setBootLogoUrl(next);
    } catch {
      /* leave the placeholder in place */
    }
  }, []);

  useEffect(() => {
    void (async () => {
      try {
        const res = await apiFetchWithRetry('/api/branding', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as BrandingConfig;
          setConfig(data);
          setSavedAppearance(appearanceOf(data));
          if (data.logoBlobPath) await loadBootPreview();
        }
      } catch { /* fallback to defaults */ }
      finally { setLoading(false); }
    })();
  }, [loadBootPreview]);

  // Revoke the boot preview object URL on unmount.
  useEffect(() => () => {
    if (bootLogoUrlRef.current) URL.revokeObjectURL(bootLogoUrlRef.current);
  }, []);

  const handleSave = async () => {
    if (hasValidationError) return;
    setSaveStatus('loading');
    const toastId = notify({ status: 'loading', title: 'Saving branding settings…' });
    try {
      const res = await apiFetch('/api/branding', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({
          primaryColor: config.primaryColor,
          accentColor: config.accentColor,
          applicationName: config.applicationName,
        }),
      });
      if (res.ok || res.status === 204) {
        await refresh();
        setSavedAppearance(appearanceOf(config));
        setSaveStatus('success');
        update(toastId, {
          status: 'success',
          title: 'Branding settings saved',
          description: 'The portal appearance has been updated.',
        });
        setTimeout(() => setSaveStatus('idle'), 1600);
      } else {
        setSaveStatus('error');
        update(toastId, {
          status: 'error',
          title: 'Could not save branding settings',
          description: await extractError(res, 'Failed to save branding settings.'),
        });
        setTimeout(() => setSaveStatus('idle'), 1600);
      }
    } catch {
      setSaveStatus('error');
      update(toastId, {
        status: 'error',
        title: 'Could not save branding settings',
        description: 'A network error occurred. Please try again.',
      });
      setTimeout(() => setSaveStatus('idle'), 1600);
    }
  };

  const handleLogoSelected = (kind: LogoKind) => async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = ''; // allow re-selecting the same file later
    if (!file) return;

    if (!file.type.startsWith('image/')) {
      notify({ status: 'error', title: 'Invalid file', description: 'Please choose an image file.' });
      return;
    }
    if (file.size > MAX_LOGO_BYTES) {
      notify({ status: 'error', title: 'Logo too large', description: 'Logo must be 512 KB or smaller.' });
      return;
    }

    const endpoint = kind === 'portal' ? '/api/branding/portal-logo' : '/api/branding/logo';
    setUploadingKind(kind);
    const toastId = notify({ status: 'loading', title: 'Uploading logo…' });
    try {
      const dataBase64 = await readFileAsBase64(file);
      const res = await apiFetch(endpoint, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ fileName: file.name, contentType: file.type, dataBase64 }),
      });
      if (res.ok) {
        const updated = await res.json() as BrandingConfig;
        setConfig(prev => ({
          ...prev,
          logoBlobPath: updated.logoBlobPath,
          portalLogoBlobPath: updated.portalLogoBlobPath,
        }));
        if (kind === 'portal') {
          await refresh(); // updates the streamed logo shown in the sidebar/header
        } else {
          await loadBootPreview();
        }
        update(toastId, { status: 'success', title: 'Logo updated' });
      } else {
        update(toastId, {
          status: 'error',
          title: 'Could not upload logo',
          description: await extractError(res, 'Failed to upload the logo.'),
        });
      }
    } catch {
      update(toastId, {
        status: 'error',
        title: 'Could not upload logo',
        description: 'A network error occurred while uploading the logo.',
      });
    } finally {
      setUploadingKind(null);
    }
  };

  const resetCopyFor = (kind: LogoKind): ConfirmImpactCopy => ({
    confirmTitle: kind === 'portal' ? 'Reset portal logo?' : 'Reset boot image logo?',
    impact: kind === 'portal'
      ? 'The custom portal logo will be permanently removed and the default Cloud Imaging mark will be shown in the sidebar and header instead. This action cannot be undone.'
      : 'The custom boot image logo will be permanently removed and the default artwork will be embedded the next time boot media is built. Media already built with the custom logo is not affected, but you will need to rebuild boot media to pick up the default. This action cannot be undone.',
    confirmLabel: 'Reset to default',
    destructive: true,
  });

  const handleResetLogo = async (kind: LogoKind) => {
    setPendingReset(null);
    const endpoint = kind === 'portal' ? '/api/branding/portal-logo' : '/api/branding/logo';
    setResettingKind(kind);
    const toastId = notify({ status: 'loading', title: 'Resetting logo…' });
    try {
      const res = await apiFetch(endpoint, { method: 'DELETE', credentials: 'include' });
      if (res.ok) {
        const updated = await res.json() as BrandingConfig;
        setConfig(prev => ({
          ...prev,
          logoBlobPath: updated.logoBlobPath,
          portalLogoBlobPath: updated.portalLogoBlobPath,
        }));
        if (kind === 'portal') {
          await refresh(); // clears the streamed logo shown in the sidebar/header
        } else {
          await loadBootPreview();
        }
        update(toastId, { status: 'success', title: 'Logo reset to default' });
      } else {
        update(toastId, {
          status: 'error',
          title: 'Could not reset logo',
          description: await extractError(res, 'Failed to reset the logo.'),
        });
      }
    } catch {
      update(toastId, {
        status: 'error',
        title: 'Could not reset logo',
        description: 'A network error occurred while resetting the logo.',
      });
    } finally {
      setResettingKind(null);
    }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  return (
    <>
    <div className="max-w-2xl space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Portal logo</CardTitle>
          <CardDescription>
            Shown in the portal sidebar and header. PNG or SVG on a transparent background works best (max 512&nbsp;KB).
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
          <div className="flex flex-wrap gap-2">
            <input
              ref={portalInputRef}
              type="file"
              accept="image/*"
              className="hidden"
              onChange={handleLogoSelected('portal')}
            />
            <Button
              type="button"
              variant="outline"
              loading={uploadingKind === 'portal'}
              onClick={() => portalInputRef.current?.click()}
            >
              <Upload className="h-4 w-4" />
              {logoUrl ? 'Replace logo' : 'Upload logo'}
            </Button>
            {logoUrl && (
              <Button
                type="button"
                variant="outline"
                loading={resettingKind === 'portal'}
                onClick={() => setPendingReset('portal')}
              >
                <RotateCcw className="h-4 w-4" />
                Reset to default
              </Button>
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Boot image logo</CardTitle>
          <CardDescription>
            Embedded into the Imaging Client boot media built by the Media Builder. May use different dimensions than the portal logo. PNG on a transparent background works best (max 512&nbsp;KB).
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center gap-4">
          <div className="flex h-16 w-16 shrink-0 items-center justify-center overflow-hidden rounded-lg border border-border bg-muted">
            {bootLogoUrl ? (
              <img src={bootLogoUrl} alt="Current boot image logo" className="h-full w-full object-contain" />
            ) : (
              <ImageIcon className="h-6 w-6 text-muted-foreground" aria-hidden="true" />
            )}
          </div>
          <div className="flex flex-wrap gap-2">
            <input
              ref={bootInputRef}
              type="file"
              accept="image/*"
              className="hidden"
              onChange={handleLogoSelected('boot')}
            />
            <Button
              type="button"
              variant="outline"
              loading={uploadingKind === 'boot'}
              onClick={() => bootInputRef.current?.click()}
            >
              <Upload className="h-4 w-4" />
              {bootLogoUrl ? 'Replace logo' : 'Upload logo'}
            </Button>
            {bootLogoUrl && (
              <Button
                type="button"
                variant="outline"
                loading={resettingKind === 'boot'}
                onClick={() => setPendingReset('boot')}
              >
                <RotateCcw className="h-4 w-4" />
                Reset to default
              </Button>
            )}
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
              aria-invalid={!!applicationNameError}
            />
            {applicationNameError && (
              <p className="text-xs text-destructive">{applicationNameError}</p>
            )}
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
                  aria-invalid={!!primaryColorError}
                />
              </div>
              {primaryColorError && (
                <p className="text-xs text-destructive">{primaryColorError}</p>
              )}
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
                  aria-invalid={!!accentColorError}
                />
              </div>
              {accentColorError && (
                <p className="text-xs text-destructive">{accentColorError}</p>
              )}
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="flex items-center gap-3">
        <Button
          onClick={handleSave}
          status={saveStatus}
          variant={isDirty || saveStatus !== 'idle' ? 'default' : 'secondary'}
          disabled={saveStatus === 'loading' || (!isDirty && saveStatus === 'idle') || hasValidationError}
        >
          Save branding
        </Button>
        {isDirty && saveStatus === 'idle' && (
          <p className="text-xs text-muted-foreground">You have unsaved changes.</p>
        )}
      </div>
    </div>

      {pendingReset && (
        <ConfirmImpactDialog
          copy={resetCopyFor(pendingReset)}
          busy={resettingKind === pendingReset}
          onCancel={() => setPendingReset(null)}
          onConfirm={() => void handleResetLogo(pendingReset)}
          titleId="branding-reset-confirm-title"
        />
      )}
    </>
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

