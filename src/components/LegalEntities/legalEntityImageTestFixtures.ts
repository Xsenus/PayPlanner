// Real small PNG/JPEG encoded by the browser canvas; no substitute raster decoder.
export const pngFixtureBase64 = 'iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAWUlEQVR4AezVQQ0AIQxE0V0s4AVxCEAcXtAAEuYwIczhN+mpl/alacv3OGgAAQTyBWpv20l1ZvIF1ARuHQEEEEAAAQTyBdaYv5PqW+YLqAncOgIIXBdQS3oAAAD//1gyo7YAAAAGSURBVAMA4cpAQbU7YKYAAAAASUVORK5CYII=';
export const jpegFixtureBase64 = '/9j/4AAQSkZJRgABAQAAAQABAAD/4gHYSUNDX1BST0ZJTEUAAQEAAAHIAAAAAAQwAABtbnRyUkdCIFhZWiAH4AABAAEAAAAAAABhY3NwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAA9tYAAQAAAADTLQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAlkZXNjAAAA8AAAACRyWFlaAAABFAAAABRnWFlaAAABKAAAABRiWFlaAAABPAAAABR3dHB0AAABUAAAABRyVFJDAAABZAAAAChnVFJDAAABZAAAAChiVFJDAAABZAAAAChjcHJ0AAABjAAAADxtbHVjAAAAAAAAAAEAAAAMZW5VUwAAAAgAAAAcAHMAUgBHAEJYWVogAAAAAAAAb6IAADj1AAADkFhZWiAAAAAAAABimQAAt4UAABjaWFlaIAAAAAAAACSgAAAPhAAAts9YWVogAAAAAAAA9tYAAQAAAADTLXBhcmEAAAAAAAQAAAACZmYAAPKnAAANWQAAE9AAAApbAAAAAAAAAABtbHVjAAAAAAAAAAEAAAAMZW5VUwAAACAAAAAcAEcAbwBvAGcAbABlACAASQBuAGMALgAgADIAMAAxADb/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAgACADASIAAhEBAxEB/8QAFgABAQEAAAAAAAAAAAAAAAAACAAJ/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/EABYBAQEBAAAAAAAAAAAAAAAAAAAEBf/EACERAAEDAQkAAAAAAAAAAAAAABEABBIFARMVISIxQVFh/9oADAMBAAIRAxEAPwDKpI3UjhxcDIlYFVquGQ0SkeRsPLe0IkboRDdxfnIBKVVcTnojEcnc+WdKN0IkOG9+MwEqtKxOGuMTwdx7Z0m6ESQ3b3BzJSlUrDJ65SHA2Ptva//Z';

export function imageFixtureBytes(format: 'png' | 'jpeg' = 'png'): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(atob(format === 'png' ? pngFixtureBase64 : jpegFixtureBase64), char => char.charCodeAt(0));
}

export function imageFixtureFile(name: string): File {
  const png = imageFixtureBytes();
  const data = new TextEncoder().encode(`fixture\0${name}`);
  const chunk = new Uint8Array(data.length + 12);
  new DataView(chunk.buffer).setUint32(0, data.length);
  chunk.set([116, 69, 88, 116], 4); // tEXt, preserving a valid PNG and distinct file contents.
  chunk.set(data, 8);
  let crc = 0xffffffff;
  for (const byte of chunk.subarray(4, chunk.length - 4)) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  new DataView(chunk.buffer).setUint32(chunk.length - 4, (crc ^ 0xffffffff) >>> 0);
  return new File([png.slice(0, 33).buffer, chunk.buffer, png.slice(33).buffer], name, { type: 'image/png' });
}

// JSDOM lacks Blob.arrayBuffer; the polyfill reads real fixture bytes using its native reader.
// Capture that reader before individual tests replace the application's FileReader.
export function installBlobArrayBufferForTests(): () => void {
  if (typeof Blob.prototype.arrayBuffer === 'function') return () => {};
  const NativeReader = FileReader;
  Object.defineProperty(Blob.prototype, 'arrayBuffer', { configurable: true, value(this: Blob) {
    return new Promise<ArrayBuffer>((resolve, reject) => {
      const reader = new NativeReader();
      reader.onerror = () => reject(new Error('Fixture bytes could not be read.'));
      reader.onload = () => resolve(reader.result as ArrayBuffer);
      reader.readAsArrayBuffer(this);
    });
  } });
  return () => { Reflect.deleteProperty(Blob.prototype, 'arrayBuffer'); };
}
