import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { imageFixtureBytes, imageFixtureFile, installBlobArrayBufferForTests } from './legalEntityImageTestFixtures';
import { inspectLegalEntityImageHeader, inspectLegalEntityImageSource, LEGAL_ENTITY_IMAGE_HEADER_MAX_BYTES, validateLegalEntityImageDimensions } from './legalEntityImageSourceUtils';
import { getLegalEntityImageSourceHint, LEGAL_ENTITY_RASTER_SOURCE_MAX_BYTES, validateLegalEntityImageSourceFile } from './legalEntityImageUtils';

let restoreArrayBuffer: () => void;
beforeAll(() => { restoreArrayBuffer = installBlobArrayBufferForTests(); });
afterAll(() => { restoreArrayBuffer(); });
const inspect = (bytes: Uint8Array, kind: 'logo' | 'seal' | 'signature' = 'logo', total = bytes.length) => inspectLegalEntityImageHeader(bytes, total, kind);
const sizedPng = (width: number, height: number) => {
  const bytes = imageFixtureBytes();
  const view = new DataView(bytes.buffer);
  view.setUint32(16, width); view.setUint32(20, height);
  return bytes;
};
function insertChunk(type: string, data: Uint8Array): Uint8Array<ArrayBuffer> {
  const png = imageFixtureBytes();
  const chunk = new Uint8Array(data.length + 12);
  new DataView(chunk.buffer).setUint32(0, data.length);
  chunk.set([...type].map(char => char.charCodeAt(0)), 4);
  chunk.set(data, 8);
  // Header tests only. Native decoding/CRC is deliberately not simulated here.
  const file = new Uint8Array(png.length + chunk.length);
  file.set(png.subarray(0, 33)); file.set(chunk, 33); file.set(png.subarray(33), 33 + chunk.length);
  return file;
}

describe('source image header guard', () => {
  it.each(['logo', 'seal', 'signature'] as const)('reads a real PNG header for %s', kind => {
    expect(inspect(imageFixtureBytes(), kind)).toEqual({ format: 'png', width: 32, height: 32 });
  });
  it('reads a real JPEG and progressive SOF dimensions', () => {
    const jpeg = imageFixtureBytes('jpeg');
    expect(inspect(jpeg)).toEqual({ format: 'jpeg', width: 32, height: 32 });
    const frame = jpeg.findIndex((byte, index) => byte === 0xc0 && jpeg[index - 1] === 0xff);
    expect(frame).toBeGreaterThan(0);
    jpeg[frame] = 0xc2;
    expect(inspect(jpeg)).toMatchObject({ format: 'jpeg', width: 32, height: 32 });
  });
  it('respects a Uint8Array byte offset', () => {
    const png = imageFixtureBytes();
    const data = new Uint8Array(png.length + 8); data.set(png, 8);
    expect(inspect(data.subarray(8))).toMatchObject({ width: 32, height: 32 });
  });
  it.each([[4097, 1], [1, 4097], [2000, 2001], [65535, 65535]])('rejects oversized PNG header %s x %s before native decode', (width, height) => {
    expect(() => inspect(sizedPng(width, height))).toThrow('4096 пикселей');
  });
  it('accepts the inclusive four-megapixel metadata boundary', () => {
    expect(inspect(sizedPng(2000, 2000))).toMatchObject({ width: 2000, height: 2000 });
  });
  it.each([[0, 1], [1, 0], [Number.NaN, 10], [Infinity, 1], [1.5, 2]])('rejects invalid decoded dimensions %s/%s', (width, height) => {
    expect(() => validateLegalEntityImageDimensions(width, height, 'seal')).toThrow('определить размер');
  });
  it.each(['seal', 'signature'] as const)('rejects actual JPEG source for %s', kind => {
    expect(() => inspect(imageFixtureBytes('jpeg'), kind)).toThrow('нужен PNG');
  });
  it('rejects declared PNG animation, but does not search chunk contents for acTL', () => {
    expect(() => inspect(insertChunk('acTL', new Uint8Array(8)))).toThrow('анимированный PNG');
    expect(inspect(insertChunk('tEXt', new TextEncoder().encode('comment\0acTL')))).toMatchObject({ width: 32, height: 32 });
  });
  it('rejects invalid chunk lengths without overflow or allocation', () => {
    const png = imageFixtureBytes(); new DataView(png.buffer).setUint32(33, 0xffffffff);
    expect(() => inspect(png)).toThrow('неполный');
  });
  it.each([0, 1, 7, 8, 16, 24, 32])('rejects truncated PNG header of %s bytes', length => {
    expect(() => inspect(imageFixtureBytes().subarray(0, length))).toThrow(Error);
  });
  it('bounds JPEG scanning even with a large sequence of APP segments', () => {
    const header = new Uint8Array(LEGAL_ENTITY_IMAGE_HEADER_MAX_BYTES);
    header.set([0xff, 0xd8]);
    for (let offset = 2; offset + 4 <= header.length; offset += 65537) header.set([0xff, 0xe1, 0xff, 0xff], offset);
    expect(() => inspect(header, 'logo', header.length + 65535)).toThrow('заголовок изображения слишком большой');
  });
  it.each([[0xff, 0xd8, 0xff], [0xff, 0xd8, 0xff, 0xe1, 0, 1], [0xff, 0xd8, 0xff, 0xda]])('rejects damaged JPEG markers %j', (...bytes: number[]) => {
    expect(() => inspect(Uint8Array.from(bytes))).toThrow(Error);
  });
  it.each(['seal', 'signature'] as const)('bounds source bytes for %s without changing the logo budget', kind => {
    const file = { type: 'image/png', name: 'stamp.png', size: LEGAL_ENTITY_RASTER_SOURCE_MAX_BYTES };
    expect(() => validateLegalEntityImageSourceFile(file, kind)).not.toThrow();
    expect(() => validateLegalEntityImageSourceFile({ ...file, size: file.size + 1 }, kind)).toThrow('16 МБ');
    expect(getLegalEntityImageSourceHint(kind)).toContain('4 мегапикселей');
    expect(getLegalEntityImageSourceHint(kind)).toContain('16 МБ');
  });
  it('reads only the bounded file slice and accepts a typeless real PNG', async () => {
    const file = new File([imageFixtureBytes().buffer], 'source.PNG');
    const slice = vi.spyOn(file, 'slice');
    expect(await inspectLegalEntityImageSource(file, 'logo')).toMatchObject({ format: 'png', width: 32 });
    expect(slice).toHaveBeenCalledExactlyOnceWith(0, LEGAL_ENTITY_IMAGE_HEADER_MAX_BYTES);
  });
  it('rejects a MIME-spoofed real JPEG before full file read/decode', async () => {
    const file = new File([imageFixtureBytes('jpeg').buffer], 'renamed.png', { type: 'image/png' });
    await expect(inspectLegalEntityImageSource(file, 'logo')).rejects.toThrow('не соответствует');
  });
  it('reports an actual header-read failure', async () => {
    const file = imageFixtureFile('read.png');
    vi.spyOn(file, 'slice').mockReturnValue({ arrayBuffer: () => Promise.reject(new Error('read error')) } as Blob);
    await expect(inspectLegalEntityImageSource(file, 'logo')).rejects.toThrow('прочитать заголовок');
  });
  it('ignores a late bounded header read after cancellation', async () => {
    const file = imageFixtureFile('slow.png');
    const controller = new AbortController();
    let release!: (value: ArrayBuffer) => void;
    vi.spyOn(file, 'slice').mockReturnValue({ arrayBuffer: () => new Promise<ArrayBuffer>(resolve => { release = resolve; }) } as Blob);
    const pending = inspectLegalEntityImageSource(file, 'logo', controller.signal);
    controller.abort(); release(imageFixtureBytes().buffer);
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });
  it('does not read a file when already cancelled', async () => {
    const file = imageFixtureFile('cancelled.png'); const slice = vi.spyOn(file, 'slice');
    const controller = new AbortController(); controller.abort();
    await expect(inspectLegalEntityImageSource(file, 'logo', controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    expect(slice).not.toHaveBeenCalled();
  });
});
