import { describe, it, expect } from 'vitest';
import { assignableImages, compatibleSessions } from '../../../src/cloud-imaging-portal/client/src/lib/assignmentCompatibility.ts';
import { countByArchitecture } from '../../../src/cloud-imaging-portal/client/src/lib/wimMetadata.ts';

const x64Device = { id: 'a', architecture: 'x64' as const };
const armDevice = { id: 'b', architecture: 'arm64' as const };
const legacyDevice = { id: 'c' }; // predates architecture tracking: x64

const x64Image = { imageId: 'win-x64', architecture: 'x64' as const };
const armImage = { imageId: 'win-arm', architecture: 'arm64' as const };

describe('OS image assignment compatibility', () => {
  it('assigns an image only to devices of its own architecture', () => {
    expect(compatibleSessions([x64Device, armDevice, legacyDevice], armImage)).toEqual([armDevice]);
    expect(compatibleSessions([x64Device, armDevice, legacyDevice], x64Image)).toEqual([x64Device, legacyDevice]);
  });

  it('targets nothing until an image is chosen', () => {
    expect(compatibleSessions([x64Device], undefined)).toEqual([]);
  });

  it('offers only images that match at least one coupled device', () => {
    expect(assignableImages([x64Image, armImage], [armDevice])).toEqual([armImage]);
    expect(assignableImages([x64Image, armImage], [armDevice, legacyDevice])).toEqual([x64Image, armImage]);
  });

  it('offers every image when nothing is coupled', () => {
    expect(assignableImages([x64Image, armImage], [])).toEqual([x64Image, armImage]);
  });

  it('counts catalog capacity per architecture', () => {
    expect(countByArchitecture([x64Image, armImage, {}])).toEqual({ x64: 2, arm64: 1 });
  });
});
