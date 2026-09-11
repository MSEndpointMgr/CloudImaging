export function formatOsImageInventoryName(name: string, version: string): string {
  return `${name} (${version})`;
}

export function formatImageVersion(version: string): string {
  return version;
}