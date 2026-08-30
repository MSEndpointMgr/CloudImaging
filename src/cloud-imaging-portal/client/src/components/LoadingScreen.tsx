import { HardDriveDownload } from 'lucide-react';
import { useBranding } from '../context/brandingContext.tsx';
import { cn } from '../lib/utils.ts';

/**
 * Full-screen branded splash shown while the app shell or a code-split route bundle
 * is still loading (initial load, refresh, or a token-renewal redirect round-trip).
 * Reuses the same logo/application-name fallback logic as the sidebar and header.
 */
export function LoadingScreen(): React.ReactElement {
  const { branding, logoUrl, isLoaded } = useBranding();
  const appName = branding.applicationName ?? 'Cloud Imaging';

  return (
    <div className="flex h-screen w-full flex-col items-center justify-center gap-4 bg-background text-foreground">
      {/* Held back until branding resolves so a tenant with a custom logo never sees the built-in
          one flash first. The slot keeps its size either way, so nothing shifts when it appears. */}
      <div className={cn('flex flex-col items-center gap-4 transition-opacity duration-200', isLoaded ? 'opacity-100' : 'opacity-0')}>
        {logoUrl ? (
          <img src={logoUrl} alt="" className="h-20 w-20 rounded-2xl object-contain" />
        ) : (
          <span className="flex h-20 w-20 items-center justify-center rounded-2xl bg-primary text-primary-foreground shadow-lg">
            <HardDriveDownload size={40} />
          </span>
        )}
        <p className="text-lg font-semibold">{appName}</p>
      </div>
      <div className="h-1 w-40 overflow-hidden rounded-full bg-muted">
        <div className="h-full w-1/2 rounded-full bg-primary animate-loading-bar" />
      </div>
    </div>
  );
}
