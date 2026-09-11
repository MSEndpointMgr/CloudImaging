export function formatOsImageInventoryName(name: string, version: string): string {
  const trimmedVersion = version.trim();
  return trimmedVersion ? `${name} (${trimmedVersion})` : name;
}

export function formatImageVersion(version: string): string {
  return version;
}