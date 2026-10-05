import type { ImageArchitecture } from './wimMetadata.ts';

interface HasArchitecture {
  architecture?: ImageArchitecture | null;
}

/** Missing architecture predates architecture tracking and is x64. */
export function architectureOf(item: HasArchitecture): ImageArchitecture {
  return item.architecture === 'arm64' ? 'arm64' : 'x64';
}

/** Sessions an image can be assigned to; the backend rejects any other combination. */
export function compatibleSessions<T extends HasArchitecture>(sessions: T[], image: HasArchitecture | undefined): T[] {
  if (!image) return [];
  const target = architectureOf(image);
  return sessions.filter(s => architectureOf(s) === target);
}

/** Images worth offering for a set of devices: only those matching at least one device's architecture. */
export function assignableImages<T extends HasArchitecture>(images: T[], sessions: HasArchitecture[]): T[] {
  if (sessions.length === 0) return images;
  const present = new Set(sessions.map(architectureOf));
  return images.filter(img => present.has(architectureOf(img)));
}
