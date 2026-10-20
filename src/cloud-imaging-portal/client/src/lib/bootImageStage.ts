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
  /** Absent on entries that predate Windows RE images; those are Windows PE. */
  base?: string;
  isLatestPublished: boolean;
  isProduction?: boolean;
  createdAt: string;
  promotedAt?: string | null;
}

/** "Latest" is tracked per architecture and base, so a Wi-Fi stick always updates to a Wi-Fi image. */
export function sameStream(a: CatalogEntry, b: CatalogEntry): boolean {
  return (a.architecture ?? 'x64') === (b.architecture ?? 'x64') && (a.base ?? 'WindowsPE') === (b.base ?? 'WindowsPE');
}

/**
 * The image that becomes latest again when `candidate` is demoted, mirroring Imaging Core's
 * BootImageRepository.PlanDemote: the most recently promoted other production image of the same
 * architecture and base. Undefined when `candidate` is not the latest or no such image exists.
 */
export function demoteFallback<T extends CatalogEntry>(images: T[], candidate: CatalogEntry): T | undefined {
  if (!candidate.isLatestPublished) return undefined;
  return images
    .filter(img => img.bootImageId !== candidate.bootImageId
      && sameStream(img, candidate)
      && bootImageStage(img) !== 'preProduction')
    .sort((a, b) => Date.parse(b.promotedAt ?? b.createdAt) - Date.parse(a.promotedAt ?? a.createdAt))[0];
}
