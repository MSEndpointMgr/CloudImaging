import { HardDriveDownload } from 'lucide-react';
import { useBranding } from '../context/brandingContext.tsx';
import { cn } from '../lib/utils';

export interface BrandMarkProps {
  /**
   * `lg` (default) is for full-screen states that have nothing else on them.
   * `sm` is for inline states that sit inside the app shell, where an 80px mark
   * would out-weigh the page chrome around it.
   */
  size?: 'sm' | 'lg';
}

/**
 * The tenant's portal logo and application name, or the built-in mark when no logo is
 * configured. Shared by every loading / error state (loading splash, route error screen,
 * in-page loading indicator) so a tenant that has configured branding never sees the
 * built-in mark on one screen and its own logo on another.
 */
export function BrandMark({ size = 'lg' }: BrandMarkProps = {}): React.ReactElement {
  const { branding, logoUrl } = useBranding();
  const isSmall = size === 'sm';

  return (
    <div className={cn('flex flex-col items-center', isSmall ? 'gap-3' : 'gap-4')}>
      {logoUrl ? (
        <img
          src={logoUrl}
          alt=""
          className={cn('object-contain', isSmall ? 'h-12 w-12 rounded-xl' : 'h-20 w-20 rounded-2xl')}
        />
      ) : (
        <span
          className={cn(
            'flex items-center justify-center bg-primary text-primary-foreground shadow-lg',
            isSmall ? 'h-12 w-12 rounded-xl' : 'h-20 w-20 rounded-2xl',
          )}
        >
          <HardDriveDownload size={isSmall ? 24 : 40} />
        </span>
      )}
      <p className={cn('font-semibold', isSmall ? 'text-base' : 'text-lg')}>
        {branding.applicationName ?? 'Cloud Imaging'}
      </p>
    </div>
  );
}
