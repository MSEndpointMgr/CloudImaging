import { BrandMark } from './BrandMark.tsx';

/**
 * Inline loading indicator for a page's own data fetch (e.g. Branding/Configuration reading
 * their settings from the API after the route itself has already rendered). Sits within the
 * page's normal content area, so it uses the compact `BrandMark` rather than the 80px one
 * `LoadingScreen` uses for the full-screen splash.
 *
 * The mark is painted unconditionally — including the built-in fallback when the tenant has
 * no logo, or when `/api/branding` cannot be reached at all. Gating it on resolved branding
 * left this area empty apart from the progress bar.
 */
export function PageLoading(): React.ReactElement {
  return (
    <div className="flex min-h-[40vh] w-full flex-col items-center justify-center gap-4">
      <BrandMark size="sm" />
      <div className="h-1 w-40 overflow-hidden rounded-full bg-muted">
        <div className="h-full w-1/2 rounded-full bg-primary animate-loading-bar" />
      </div>
    </div>
  );
}
