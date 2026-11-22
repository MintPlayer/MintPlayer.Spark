// Container hygiene gate (#700 PRD C1, §0 item 14): fails when a Trivy JSON report (`trivy image --format json
// --list-all-pkgs`) shows an openssh-server package or CVE-2026-123158 (*sshole: musl crypt() + sshd without PAM accepts any
// password). No container of this repository may run sshd, whatever its base image.
//
//   node tools/ci/trivy-no-sshd.mjs <image name> <report.json>
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export const BANNED_CVES = ['CVE-2026-123158'];

/** The problems in one report: packages named openssh-server*, and the banned CVEs. Empty means clean. */
export function findings(report) {
  const problems = [];
  for (const result of report?.Results ?? []) {
    for (const pkg of result.Packages ?? []) {
      if (/^openssh-server/i.test(pkg.Name ?? '')) problems.push(`package ${pkg.Name} ${pkg.Version ?? ''} in ${result.Target}`.trim());
    }
    for (const vuln of result.Vulnerabilities ?? []) {
      if (BANNED_CVES.includes(vuln.VulnerabilityID)) problems.push(`${vuln.VulnerabilityID} in ${vuln.PkgName} ${vuln.InstalledVersion ?? ''}`.trim());
    }
  }
  return problems;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [image, file] = process.argv.slice(2);
  const problems = findings(JSON.parse(readFileSync(file, 'utf8')));
  if (problems.length > 0) {
    for (const problem of problems) console.error(`::error title=sshd in ${image}::${problem}`);
    process.exit(1);
  }
  console.log(`${image}: no openssh-server, none of ${BANNED_CVES.join(', ')}`);
}
