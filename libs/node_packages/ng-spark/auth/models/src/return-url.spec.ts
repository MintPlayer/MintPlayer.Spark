import { isSafeReturnUrl, sanitizeReturnUrl } from './return-url';

/**
 * R2-H9 — the shared returnUrl validator must accept only local in-app paths
 * and reject every external / protocol-relative / control-char shape. Keep
 * these assertions in lock-step with the framework-side
 * SanitizeReturnUrl in MintPlayer.Spark.Authorization.
 */
describe('sanitizeReturnUrl (R2-H9)', () => {
  describe('isSafeReturnUrl', () => {
    it('accepts in-app relative paths', () => {
      expect(isSafeReturnUrl('/')).toBe(true);
      expect(isSafeReturnUrl('/dashboard')).toBe(true);
      expect(isSafeReturnUrl('/items/123?tab=details')).toBe(true);
      expect(isSafeReturnUrl('/items/123#section')).toBe(true);
    });

    it('rejects null/undefined/empty', () => {
      expect(isSafeReturnUrl(null)).toBe(false);
      expect(isSafeReturnUrl(undefined)).toBe(false);
      expect(isSafeReturnUrl('')).toBe(false);
    });

    it('rejects protocol-relative URLs', () => {
      expect(isSafeReturnUrl('//attacker.test')).toBe(false);
      expect(isSafeReturnUrl('//attacker.test/phish')).toBe(false);
    });

    it('rejects backslash-confused-as-slash URLs', () => {
      expect(isSafeReturnUrl('/\\attacker.test')).toBe(false);
    });

    it('rejects absolute http(s) URLs', () => {
      expect(isSafeReturnUrl('https://attacker.test')).toBe(false);
      expect(isSafeReturnUrl('http://attacker.test')).toBe(false);
    });

    it('rejects javascript: URLs', () => {
      expect(isSafeReturnUrl('javascript:alert(1)')).toBe(false);
    });

    it('rejects values containing CR/LF (header-splitting defense)', () => {
      expect(isSafeReturnUrl('/path\r\nLocation: evil')).toBe(false);
      expect(isSafeReturnUrl('/path\nLocation: evil')).toBe(false);
      expect(isSafeReturnUrl('/path\rfoo')).toBe(false);
    });

    it('rejects paths that do not start with /', () => {
      expect(isSafeReturnUrl('dashboard')).toBe(false);
      expect(isSafeReturnUrl('./dashboard')).toBe(false);
      expect(isSafeReturnUrl('../dashboard')).toBe(false);
    });
  });

  describe('sanitizeReturnUrl', () => {
    it('returns the input when safe', () => {
      expect(sanitizeReturnUrl('/dashboard', '/')).toBe('/dashboard');
    });

    it('returns the default for every hostile shape', () => {
      const defaultUrl = '/login';
      expect(sanitizeReturnUrl('//attacker.test', defaultUrl)).toBe(defaultUrl);
      expect(sanitizeReturnUrl('https://attacker.test', defaultUrl)).toBe(defaultUrl);
      expect(sanitizeReturnUrl('javascript:alert(1)', defaultUrl)).toBe(defaultUrl);
      expect(sanitizeReturnUrl(null, defaultUrl)).toBe(defaultUrl);
      expect(sanitizeReturnUrl(undefined, defaultUrl)).toBe(defaultUrl);
      expect(sanitizeReturnUrl('', defaultUrl)).toBe(defaultUrl);
    });
  });
});
