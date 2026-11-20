import { encodeAni } from './ani';
import { encodeApng } from './apng';
import { encodeGif } from './gif';
import { decodeDib, decodeIcon, encodeDib, encodeIcon } from './ico';
import { crc32, decodePng, encodePng, type Rgba } from './png';
import { imageChunks, muxAnimatedWebp } from './webp';

/** A small image with every alpha class: opaque red, half-transparent green, transparent, opaque blue. */
function sample(width = 3, height = 2): Rgba {
  const data = new Uint8Array(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    const kind = i % 4;
    data.set(kind === 0 ? [255, 0, 0, 255] : kind === 1 ? [0, 255, 0, 128] : kind === 2 ? [0, 0, 0, 0] : [0, 0, 255, 255], i * 4);
  }
  return { width, height, data };
}

const u32le = (b: Uint8Array, o: number) => new DataView(b.buffer, b.byteOffset).getUint32(o, true);
const u16le = (b: Uint8Array, o: number) => new DataView(b.buffer, b.byteOffset).getUint16(o, true);
const ascii = (b: Uint8Array, o: number, n = 4) => String.fromCharCode(...b.subarray(o, o + n));

describe('PNG', () => {
  it('computes the standard CRC-32', () => {
    // CRC-32 of "IEND" is the constant every PNG ends with.
    expect((crc32(new TextEncoder().encode('IEND')) ^ 0xffffffff) >>> 0).toBe(0xae426082);
  });

  it('round-trips RGBA through our encoder and decoder', async () => {
    const img = sample(7, 5);
    const png = await encodePng(img);
    expect([...png.subarray(0, 8)]).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    const back = await decodePng(png);
    expect(back.width).toBe(7);
    expect(back.height).toBe(5);
    expect([...back.data]).toEqual([...img.data]);
  });
});

describe('ICO and CUR', () => {
  it('writes the directory as the format requires, 256 px as 0 and as PNG', async () => {
    const ico = await encodeIcon([{ image: sample(256, 256), encoding: 'bmp' }, { image: sample(16, 16), encoding: 'bmp' }]);
    expect(u16le(ico, 0)).toBe(0);
    expect(u16le(ico, 2)).toBe(1);
    expect(u16le(ico, 4)).toBe(2);
    // Sorted small to large; 256 is stored as 0.
    expect(ico[6]).toBe(16);
    expect(ico[6 + 16]).toBe(0);
    expect(u16le(ico, 6 + 4)).toBe(1); // planes
    expect(u16le(ico, 6 + 6)).toBe(32); // bit count
    const second = u32le(ico, 6 + 16 + 12);
    expect([...ico.subarray(second, second + 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
  });

  it('stores a cursor hotspot in the planes and bit-count fields', async () => {
    const cur = await encodeIcon([{ image: sample(32, 32), encoding: 'png', hotspot: { x: 5, y: 9 } }], 'cur');
    expect(u16le(cur, 2)).toBe(2);
    expect(u16le(cur, 6 + 4)).toBe(5);
    expect(u16le(cur, 6 + 6)).toBe(9);
  });

  it('writes a DIB with doubled height, bottom-up BGRA and an AND mask', () => {
    const img = sample(3, 2);
    const dib = encodeDib(img);
    expect(u32le(dib, 0)).toBe(40);
    expect(new DataView(dib.buffer).getInt32(8, true)).toBe(4);
    // The first stored row is the image's last row; pixel (0,1) is index 3: blue.
    expect([...dib.subarray(40, 44)]).toEqual([255, 0, 0, 255]);
    // Rows of 3 px: the mask is 4 bytes per row; pixel 2 (transparent) has its bit set in the bottom row (index 5).
    const mask = 40 + 3 * 2 * 4;
    expect(dib.length).toBe(mask + 8);
    expect(dib[mask + 4] & 0x80 >> 2).toBeTruthy();
    expect([...decodeDib(dib).data]).toEqual([...img.data]);
  });

  it('reads back what it writes, PNG and BMP entries alike', async () => {
    const small = sample(16, 16), big = sample(48, 48);
    const decoded = await decodeIcon(await encodeIcon([{ image: small, encoding: 'bmp' }, { image: big, encoding: 'png', hotspot: { x: 1, y: 2 } }], 'cur'));
    expect(decoded.kind).toBe('cur');
    expect(decoded.images.map(i => i.width)).toEqual([16, 48]);
    expect([...decoded.images[0].data]).toEqual([...small.data]);
    expect([...decoded.images[1].data]).toEqual([...big.data]);
    expect(decoded.images[1].hotspot).toEqual({ x: 1, y: 2 });
  });
});

describe('ANI', () => {
  it('wraps .cur frames in RIFF ACON with anih and a fram list', async () => {
    const cur = await encodeIcon([{ image: sample(32, 32), encoding: 'png' }], 'cur');
    const ani = encodeAni([cur, cur, cur], { jiffies: 5, width: 32, height: 32 });
    expect(ascii(ani, 0)).toBe('RIFF');
    expect(u32le(ani, 4)).toBe(ani.length - 8);
    expect(ascii(ani, 8)).toBe('ACON');
    expect(ascii(ani, 12)).toBe('anih');
    expect(u32le(ani, 20 + 4)).toBe(3); // frames
    expect(u32le(ani, 20 + 28)).toBe(5); // rate in jiffies
    expect(u32le(ani, 20 + 32)).toBe(1); // AF_ICON
    expect(ascii(ani, 56)).toBe('LIST');
    expect(ascii(ani, 64)).toBe('fram');
    expect(ascii(ani, 68)).toBe('icon');
  });
});

describe('APNG', () => {
  it('writes acTL, one fcTL per frame and fdAT after the first', async () => {
    const png = await encodeApng([sample(4, 4), sample(4, 4), sample(4, 4)], { delayMs: 100, loops: 0 });
    const chunks: string[] = [];
    let o = 8;
    const view = new DataView(png.buffer);
    while (o < png.length) {
      chunks.push(ascii(png, o + 4));
      o += 12 + view.getUint32(o);
    }
    expect(chunks).toEqual(['IHDR', 'acTL', 'fcTL', 'IDAT', 'fcTL', 'fdAT', 'fcTL', 'fdAT', 'IEND']);
  });
});

describe('animated WebP mux', () => {
  it('wraps each still frame in ANMF under VP8X with the animation flag', () => {
    // A minimal still: RIFF WEBP with one VP8L chunk (contents are not decoded by the muxer).
    const vp8l = new Uint8Array([0x56, 0x50, 0x38, 0x4c, 3, 0, 0, 0, 1, 2, 3, 0]);
    const still = new Uint8Array(12 + vp8l.length);
    still.set(new TextEncoder().encode('RIFF'), 0);
    new DataView(still.buffer).setUint32(4, 4 + vp8l.length, true);
    still.set(new TextEncoder().encode('WEBP'), 8);
    still.set(vp8l, 12);
    expect([...imageChunks(still)]).toEqual([...vp8l]);

    const anim = muxAnimatedWebp([still, still], { width: 10, height: 20, delayMs: 80, loops: 0 });
    expect(ascii(anim, 0)).toBe('RIFF');
    expect(ascii(anim, 8)).toBe('WEBP');
    expect(ascii(anim, 12)).toBe('VP8X');
    expect(anim[20] & 0x02).toBe(0x02);
    expect(anim[24] | (anim[25] << 8) | (anim[26] << 16)).toBe(9); // width - 1
    expect(ascii(anim, 30)).toBe('ANIM');
    expect(ascii(anim, 44)).toBe('ANMF');
  });
});

describe('GIF', () => {
  it('writes GIF89a with a looping animation and a transparent index', () => {
    const gif = encodeGif([sample(8, 8), sample(8, 8)], { delayMs: 100, loop: true, alphaThreshold: 128, matte: '#ffffff' });
    expect(ascii(gif, 0, 6)).toBe('GIF89a');
    const text = String.fromCharCode(...gif);
    expect(text).toContain('NETSCAPE2.0');
    // Two graphic control extensions (one per frame), with the transparency flag set.
    const gce = [...text.matchAll(/\x21\xf9\x04/g)].map(m => m.index!);
    expect(gce.length).toBe(2);
    expect(gif[gce[0] + 3] & 0x01).toBe(1);
  });
});
