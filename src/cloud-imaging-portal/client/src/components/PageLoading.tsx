/**
 * Inline loading indicator for a page's own data fetch (e.g. Branding/Configuration reading their
 * settings from the API after the route itself has already rendered). Unlike `LoadingScreen`
 * (full-screen splash with logo, used only for auth redirect / route-chunk loading), this has no
 * logo and sits within the page's normal content area.
 */
export function PageLoading(): React.ReactElement {
  return (
    <div className="flex min-h-[40vh] w-full flex-col items-center justify-center gap-4">
      <div className="h-1 w-40 overflow-hidden rounded-full bg-muted">
        <div className="h-full w-1/2 rounded-full bg-primary animate-loading-bar" />
      </div>
    </div>
  );
}
