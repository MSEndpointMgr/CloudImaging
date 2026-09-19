export function formatOsImageInventoryName(name: string, version: string): string {
  return version ? `${name} (${version})` : name;
}

export function formatImageVersion(version: string): string {
  return version;
}