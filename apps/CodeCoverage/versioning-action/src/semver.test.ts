import { compare, distTag, highest, parse } from './semver';

describe('semver', () => {
  it('orders by SemVer precedence', () => {
    const sorted = ['1.0.0', '1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-beta.2', '1.0.0-beta.11', '0.9.9', '1.0.0-rc.1']
      .map((v) => parse(v)!)
      .sort(compare)
      .map((v) => v.text);
    expect(sorted).toEqual(['0.9.9', '1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0']);
  });

  it('ignores what is not a version', () => {
    expect(highest(['1.2.3', 'garbage', '01.2.3', '1.10.0'])?.text).toBe('1.10.0');
    expect(highest([])).toBeNull();
  });

  it.each([
    ['22.4.0', ['22.3.0', '21.9.0'], 'latest'],
    ['21.9.1', ['22.3.0', '21.9.0'], 'v21-lts'],
    ['23.0.0-rc.1', ['22.3.0'], 'next'],
    ['1.0.0', [], 'latest'],
    // A newer major only as a prerelease does not take latest from the stable line.
    ['22.4.0', ['23.0.0-rc.1', '22.3.0'], 'latest'],
  ])('%s with %j released is %s (D11)', (version, released, tag) => {
    expect(distTag(version, released)).toBe(tag);
  });
});
