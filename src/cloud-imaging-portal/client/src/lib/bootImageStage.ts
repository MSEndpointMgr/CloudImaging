/** Lifecycle stage of a boot image catalog entry. */
export type BootImageStage = 'latest' | 'production' | 'preProduction';

/** `isProduction` is absent on entries that predate the pre-production stage; those were live. */
export function bootImageStage(image: { isLatestPublished: boolean; isProduction?: boolean }): BootImageStage {
  if (image.isLatestPublished) return 'latest';
  return image.isProduction === false ? 'preProduction' : 'production';
}

export const BOOT_IMAGE_STAGE_LABELS: Record<BootImageStage, string> = {
  latest: 'Latest',
  production: 'Production',
  preProduction: 'Pre-production',
};

interface CatalogEntry {
  bootImageId: string;
  architecture?: string;
  isLatestPublished: boolean;
  isProduction?: boolean;
  createdAt: string;
  promotedAt?: string | null;
}

/**
 * The image that becomes latest again when `candidate` is demoted, mirroring Imaging Core's
 * BootImageRepository.PlanDemote: the most recently promoted other production image of the same
 * architecture. Undefined when `candidate` is not the latest or no such image exists.
 */
export function demoteFallback<T extends CatalogEntry>(images: T[], candidate: CatalogEntry): T | undefined {
  if (!candidate.isLatestPublished) return undefined;
  const arch = candidate.architecture ?? 'x64';
  return images
    .filter(img => img.bootImageId !== candidate.bootImageId
      && (img.architecture ?? 'x64') === arch
      && bootImageStage(img) !== 'preProduction')
    .sort((a, b) => Date.parse(b.promotedAt ?? b.createdAt) - Date.parse(a.promotedAt ?? a.createdAt))[0];
}
