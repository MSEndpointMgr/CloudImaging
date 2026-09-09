import { lazy, type ComponentType, type LazyExoticComponent } from 'react';

/**
 * Wraps `React.lazy` so a failed route-chunk fetch (e.g. the browser still has an old
 * `index.html`/bundle cached from before a redeploy, and the hashed chunk file it references
 * no longer exists) triggers ONE automatic full-page reload instead of leaving the app stuck
 * on the Suspense fallback forever. The reload picks up the current `index.html`, which
 * references the current chunk hashes, so the retry succeeds. If it still fails after the
 * reload (a genuinely broken deployment, not just a stale cache), the error is rethrown so
 * the nearest error boundary can show a real error message instead of reloading forever.
 */
export function lazyWithReload<T extends { default: ComponentType<object> }>(
  factory: () => Promise<T>,
): LazyExoticComponent<T['default']> {
  return lazy(async () => {
    const reloadKey = `ci-chunk-reload:${window.location.pathname}`;
    try {
      const mod = await factory();
      // Disarm the retry latch on success. Left set, a single transient failure would mean the
      // *next* unrelated failure on this path skips the automatic reload and goes straight to
      // the error screen, for the rest of the browser session.
      sessionStorage.removeItem(reloadKey);
      return mod;
    } catch (error) {
      if (!sessionStorage.getItem(reloadKey)) {
        sessionStorage.setItem(reloadKey, '1');
        window.location.reload();
        // Reload is already underway; never resolve so Suspense keeps showing its fallback
        // until the navigation completes.
        return new Promise<never>(() => {});
      }
      throw error;
    }
  });
}
