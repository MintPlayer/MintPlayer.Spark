import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findings } from './ci/trivy-no-sshd.mjs';

test('a clean report has no findings, also with no OS packages at all (a static image)', () => {
  assert.deepEqual(findings({ Results: [{ Target: 'img (debian 12)', Packages: [{ Name: 'openssl', Version: '3.0' }] }] }), []);
  assert.deepEqual(findings({}), []);
});

test('an openssh-server package fails, whatever its flavour', () => {
  const report = { Results: [{ Target: 'img (alpine 3.20)', Packages: [{ Name: 'openssh-server', Version: '9.7' }, { Name: 'openssh-server-pam', Version: '9.7' }, { Name: 'openssh-client', Version: '9.7' }] }] };
  assert.deepEqual(findings(report), ['package openssh-server 9.7 in img (alpine 3.20)', 'package openssh-server-pam 9.7 in img (alpine 3.20)']);
});

test('the sshole CVE fails even on another package name', () => {
  const report = { Results: [{ Target: 'img', Vulnerabilities: [{ VulnerabilityID: 'CVE-2026-123158', PkgName: 'openssh', InstalledVersion: '10.5' }, { VulnerabilityID: 'CVE-2024-0001', PkgName: 'zlib' }] }] };
  assert.deepEqual(findings(report), ['CVE-2026-123158 in openssh 10.5']);
});
