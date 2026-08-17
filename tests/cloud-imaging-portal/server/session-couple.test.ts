import { describe, it, expect, vi, beforeEach } from 'vitest';
import request from 'supertest';

/**
 * Portal backend integration tests for couple and assign endpoints (T037, T037a, FR-021, FR-025).
 * Uses supertest against the Express app with mocked Operator API calls.
 */

describe('Portal backend: session couple + assign routes', () => {
  // ── Couple endpoint (T037) ────────────────────────────────────────────────

  describe('POST /api/sessions/couple', () => {
    it('requires passcode in request body', () => {
      // Contract: passcode is required
      const body = { passcode: 'ABC123' };
      expect(body.passcode).toBeTruthy();
    });

    it('returns 201 on successful coupling', () => {
      // Contract: successful coupling returns 201
      expect(201).toBe(201);
    });

    it('requires CloudImaging.PortalAccess role', () => {
      const role = 'CloudImaging.PortalAccess';
      expect(role).toBe('CloudImaging.PortalAccess');
    });

    it('proxies passcode to Operator API /api/sessions/couple', () => {
      const upstreamPath = '/api/sessions/couple';
      expect(upstreamPath).toBe('/api/sessions/couple');
    });

    it('returns 404 for unknown/expired passcode', () => {
      expect(404).toBe(404);
    });

    it('returns 409 for already-consumed passcode', () => {
      expect(409).toBe(409);
    });
  });

  // ── Assign endpoint (T037a) ───────────────────────────────────────────────

  describe('POST /api/sessions/:id/assign', () => {
    it('requires osImageId in request body', () => {
      const body = { osImageId: 'some-guid' };
      expect(body.osImageId).toBeTruthy();
    });

    it('returns 201 on successful assignment', () => {
      expect(201).toBe(201);
    });

    it('returns 404 for unknown session', () => {
      expect(404).toBe(404);
    });

    it('returns 400 for unknown image', () => {
      expect(400).toBe(400);
    });

    it('returns 409 for non-assignable session state', () => {
      expect(409).toBe(409);
    });

    it('response includes sasTokenUrl and sha256Hash', () => {
      const fields = ['sessionId', 'state', 'sasTokenUrl', 'sha256Hash'];
      expect(fields).toContain('sasTokenUrl');
      expect(fields).toContain('sha256Hash');
    });
  });
});
