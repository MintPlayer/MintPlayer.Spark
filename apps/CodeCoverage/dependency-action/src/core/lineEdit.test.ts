import { blockFrom, editBytes, EditRefused, findUniqueLine, parseText, replaceUniqueToken, serializeText } from './lineEdit';

const edit = (text: string | Buffer, find: RegExp, to: string) => editBytes(Buffer.isBuffer(text) ? text : Buffer.from(text, 'utf8'), { find, to }, 'f');

describe('parseText / serializeText', () => {
  it.each([
    ['LF', 'a\nb\n'],
    ['CRLF', 'a\r\nb\r\n'],
    ['mixed', 'a\r\nb\nc\rd'],
    ['no final newline', 'a\nb'],
    ['empty', ''],
    ['only newline', '\n'],
  ])('round-trips %s byte for byte', (_n, text) => {
    const bytes = Buffer.from(text, 'utf8');
    expect(serializeText(parseText(bytes)).equals(bytes)).toBe(true);
  });

  it('keeps a UTF-8 BOM', () => {
    const bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('<Project>\r\n</Project>\r\n')]);
    const doc = parseText(bytes);
    expect(doc.bom).toBe(true);
    expect(serializeText(doc).equals(bytes)).toBe(true);
  });

  it('refuses a file that is not valid UTF-8 (an edit could not be byte-exact)', () => {
    expect(() => parseText(Buffer.from([0x61, 0xff, 0x0a]), 'x.props')).toThrow(EditRefused);
  });
});

describe('replaceUniqueToken', () => {
  it('replaces only the token and keeps CRLF, BOM and the rest of the line', () => {
    const before = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('  <PackageReference Include="A"   Version="1.0.0" /> <!-- keep -->\r\n<X/>\r\n')]);
    const after = edit(before, /Include="A"\s+Version="(?<v>1\.0\.0)"/, '1.2.0');
    expect(after.toString('utf8')).toBe('﻿  <PackageReference Include="A"   Version="1.2.0" /> <!-- keep -->\r\n<X/>\r\n');
  });

  it('refuses when nothing matches', () => {
    expect(() => edit('a = "1"\n', /b = "(?<v>1)"/, '2')).toThrow(/no line matches/);
  });

  it('refuses when more than one line matches', () => {
    try {
      edit('a = "1"\na = "1"\n', /a = "(?<v>1)"/, '2');
      throw new Error('not refused');
    } catch (e) {
      expect(e).toBeInstanceOf(EditRefused);
      expect((e as EditRefused).matches).toBe(2);
    }
  });

  it('refuses two matches on ONE line', () => {
    expect(() => edit('x 1 1\n', /(?<v>1)/, '2')).toThrow(/ambiguous/);
  });

  it('honours a line range and a line filter', () => {
    const doc = parseText(Buffer.from('[a]\nv = "1"\n[b]\nv = "1"\n'));
    replaceUniqueToken(doc, { find: /v = "(?<v>1)"/, to: '2', range: [2, 4] });
    expect(serializeText(doc).toString()).toBe('[a]\nv = "1"\n[b]\nv = "2"\n');
    const doc2 = parseText(Buffer.from('dep-a 1\ndep-b 1\n'));
    replaceUniqueToken(doc2, { find: /(?<v>1)/, to: '3', lineFilter: (t) => t.startsWith('dep-b') });
    expect(serializeText(doc2).toString()).toBe('dep-a 1\ndep-b 3\n');
  });

  it('throws a programming error for a pattern without the v group', () => {
    expect(() => edit('a\n', /a/, 'b')).toThrow(/named group "v"/);
  });
});

describe('anchors and blocks', () => {
  it('findUniqueLine refuses 0 and 2 anchors', () => {
    const doc = parseText(Buffer.from('a\nb\na\n'));
    expect(findUniqueLine(doc, /b/)).toBe(1);
    expect(() => findUniqueLine(doc, /a/)).toThrow(EditRefused);
    expect(() => findUniqueLine(doc, /c/)).toThrow(EditRefused);
  });

  it('blockFrom treats a self-closing anchor as its own block', () => {
    const doc = parseText(Buffer.from('<P Include="a" />\n<P Include="b">\n  <Version>1</Version>\n</P>\n'));
    expect(blockFrom(doc, 0, /\/>|<\/P>/)).toEqual([0, 1]);
    expect(blockFrom(doc, 1, /\/>|<\/P>/)).toEqual([1, 4]);
  });
});
