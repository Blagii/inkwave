// Cross-platform app icons for the desktop builds.
//
// The macOS icon (build/icon.icns) is the source of truth and is produced by build/make-icon.sh (macOS only). This
// script reads the PNGs embedded in that .icns and derives the two icons the other platforms need:
//
//   build/icon.png  — a 512×512 PNG (Linux / SteamOS desktop entry)
//   build/icon.ico  — a Windows icon with 16/32/48/64/128/256 px entries (BMP/DIB entries, which is what the
//                     Windows resource editor and electron-packager's resedit expect — PNG entries are not read)
//
// It is pure Node (zlib only), so it runs the same on macOS, Windows and Linux, and it never needs a browser.
//
//   node build/make-icon.cjs
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const ROOT = path.join(__dirname, '..');
const ICNS = path.join(__dirname, 'icon.icns');

// ----------------------------------------------------------------------------------------------- PNG decode
// Minimal decoder for the 8-bit RGBA PNGs that iconutil writes (colour type 6, no interlacing).
function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let off = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
  const idat = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      bitDepth = data[8]; colorType = data[9]; interlace = data[12];
    } else if (type === 'IDAT') idat.push(data);
    else if (type === 'IEND') break;
    off += 12 + len;
  }
  if (bitDepth !== 8 || colorType !== 6 || interlace !== 0) {
    throw new Error(`unsupported PNG (depth ${bitDepth}, colour ${colorType}, interlace ${interlace})`);
  }
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const bpp = 4, stride = width * bpp;
  const out = Buffer.alloc(stride * height);
  let p = 0;
  for (let y = 0; y < height; y++) {
    const filter = raw[p++];
    const line = raw.subarray(p, p + stride); p += stride;
    const cur = out.subarray(y * stride, (y + 1) * stride);
    const prev = y ? out.subarray((y - 1) * stride, y * stride) : null;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? cur[x - bpp] : 0;
      const b = prev ? prev[x] : 0;
      const c = prev && x >= bpp ? prev[x - bpp] : 0;
      let v = line[x];
      if (filter === 1) v += a;
      else if (filter === 2) v += b;
      else if (filter === 3) v += (a + b) >> 1;
      else if (filter === 4) {
        const pp = a + b - c, pa = Math.abs(pp - a), pb = Math.abs(pp - b), pc = Math.abs(pp - c);
        v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
      }
      cur[x] = v & 0xff;
    }
  }
  return { width, height, data: out };   // RGBA
}

// -------------------------------------------------------------------------------------------------- resample
// Box filter (area average) — nearest neighbour turns a rounded square into a staircase at 16 px.
function resize(img, w, h) {
  const { width: sw, height: sh, data: sd } = img;
  const out = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) {
    const y0 = Math.floor((y * sh) / h), y1 = Math.max(y0 + 1, Math.floor(((y + 1) * sh) / h));
    for (let x = 0; x < w; x++) {
      const x0 = Math.floor((x * sw) / w), x1 = Math.max(x0 + 1, Math.floor(((x + 1) * sw) / w));
      let r = 0, g = 0, b = 0, a = 0, n = 0;
      for (let yy = y0; yy < y1; yy++) {
        for (let xx = x0; xx < x1; xx++) {
          const i = (yy * sw + xx) * 4;
          r += sd[i]; g += sd[i + 1]; b += sd[i + 2]; a += sd[i + 3]; n++;
        }
      }
      const o = (y * w + x) * 4;
      out[o] = Math.round(r / n); out[o + 1] = Math.round(g / n); out[o + 2] = Math.round(b / n); out[o + 3] = Math.round(a / n);
    }
  }
  return { width: w, height: h, data: out };
}

// ------------------------------------------------------------------------------------------------------- icns
// Pull the PNG entries out of an .icns container: 4-byte type, 4-byte length, then the payload.
function readIcns(file) {
  const d = fs.readFileSync(file);
  if (d.toString('ascii', 0, 4) !== 'icns') throw new Error('not an icns file');
  const total = d.readUInt32BE(4);
  const out = [];
  let off = 8;
  while (off < total) {
    const type = d.toString('ascii', off, off + 4);
    const len = d.readUInt32BE(off + 4);
    const payload = d.subarray(off + 8, off + len);
    if (payload.readUInt32BE(0) === 0x89504e47) {
      const img = decodePng(payload);
      out.push({ type, ...img });
    }
    off += len;
  }
  return out;
}

// ------------------------------------------------------------------------------------------------------ ICO
// A classic ICO: a directory plus one BITMAPINFOHEADER + BGRA pixels + AND mask per size.
function icoEntry(img) {
  const { width: w, height: h, data } = img;
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);            // biSize
  header.writeInt32LE(w, 4);              // biWidth
  header.writeInt32LE(h * 2, 8);          // biHeight = XOR + AND
  header.writeUInt16LE(1, 12);           // biPlanes
  header.writeUInt16LE(32, 14);          // biBitCount
  header.writeUInt32LE(0, 16);           // biCompression = BI_RGB
  const xor = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) {          // bottom-up
    const src = (h - 1 - y) * w * 4;
    for (let x = 0; x < w; x++) {
      const s = src + x * 4, o = (y * w + x) * 4;
      xor[o] = data[s + 2];               // B
      xor[o + 1] = data[s + 1];           // G
      xor[o + 2] = data[s];               // R
      xor[o + 3] = data[s + 3];           // A
    }
  }
  const maskStride = Math.ceil(w / 32) * 4;
  const mask = Buffer.alloc(maskStride * h);   // all zero: the alpha channel does the work
  header.writeUInt32LE(xor.length + mask.length, 20);   // biSizeImage
  return Buffer.concat([header, xor, mask]);
}

function buildIco(images) {
  const dir = Buffer.alloc(6 + 16 * images.length);
  dir.writeUInt16LE(0, 0); dir.writeUInt16LE(1, 2); dir.writeUInt16LE(images.length, 4);
  let offset = dir.length;
  const blobs = [];
  images.forEach((img, i) => {
    const blob = icoEntry(img);
    const o = 6 + 16 * i;
    dir[o] = img.width >= 256 ? 0 : img.width;
    dir[o + 1] = img.height >= 256 ? 0 : img.height;
    dir[o + 2] = 0; dir[o + 3] = 0;
    dir.writeUInt16LE(1, o + 4); dir.writeUInt16LE(32, o + 6);
    dir.writeUInt32LE(blob.length, o + 8);
    dir.writeUInt32LE(offset, o + 12);
    offset += blob.length;
    blobs.push(blob);
  });
  return Buffer.concat([dir, ...blobs]);
}

// ------------------------------------------------------------------------------------------------------ main
function main() {
  if (!fs.existsSync(ICNS)) {
    console.error(`[icon] ${path.relative(ROOT, ICNS)} is missing — run "npm run icon:mac" on macOS first.`);
    process.exit(1);
  }
  const entries = readIcns(ICNS);
  if (!entries.length) { console.error('[icon] no PNG entries inside the .icns'); process.exit(1); }

  // the largest PNG becomes the Linux icon; the rest are resized into the ICO
  const biggest = entries.reduce((a, b) => (b.width > a.width ? b : a));
  const pngPath = path.join(__dirname, 'icon.png');
  fs.writeFileSync(pngPath, fs.readFileSync(ICNS).length ? encodePng(resize(biggest, 512, 512)) : null);
  console.log(`[icon] ${path.relative(ROOT, pngPath)} (512×512)`);

  const sizes = [16, 32, 48, 64, 128, 256];
  const icoImages = sizes.map((s) => {
    const src = entries.reduce((a, b) => (b.width >= s ? (b.width < a.width ? b : a) : a), entries[0]);
    return resize(src.width === s ? src : resize(src, s, s), s, s);
  });
  const icoPath = path.join(__dirname, 'icon.ico');
  fs.writeFileSync(icoPath, buildIco(icoImages));
  console.log(`[icon] ${path.relative(ROOT, icoPath)} (${sizes.join('/')})`);
}

// A tiny PNG encoder (8-bit RGBA, no filter) — only needed to write the Linux icon.
function encodePng(img) {
  const { width: w, height: h, data } = img;
  const stride = w * 4;
  const raw = Buffer.alloc((stride + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (stride + 1)] = 0;                       // filter: none
    data.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  const chunk = (type, payload) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(payload.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), payload]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body) >>> 0);
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0)),
  ]);
}

let CRC_TABLE = null;
function crc32(buf) {
  if (!CRC_TABLE) {
    CRC_TABLE = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
      CRC_TABLE[n] = c;
    }
  }
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

main();
