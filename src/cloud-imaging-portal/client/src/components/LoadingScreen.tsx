import { BrandMark } from './BrandMark.tsx';

/**
 * Full-screen branded splash shown while the app shell or a code-split route bundle
 * is still loading (initial load, refresh, or a token-renewal redirect round-trip).
 * Reuses the same logo/application-name fallback logic as the sidebar and header.
 */
export function LoadingScreen(): React.ReactElement {
  return (
    <div className="flex h-screen w-full flex-col items-center justify-center gap-4 bg-background text-foreground">
      {/* Always painted. This screen is shown precisely when the app has nothing else on it, so
          deferring the mark until branding resolved left it empty apart from the progress bar -
          worst of all on the hand-off to the Entra sign-in redirect, where `/api/branding` 401s
          and the answer never arrives at all. */}
      <BrandMark />
      <div className="h-1 w-40 overflow-hidden rounded-full bg-muted">
        <div className="h-full w-1/2 rounded-full bg-primary animate-loading-bar" />
      </div>
    </div>
  );
}
