import fs from 'node:fs';
import path from 'node:path';

const file = 'D:\\DSH\\resources\\app.asar';
const fd = fs.openSync(file, 'r');
const head = Buffer.alloc(16);
fs.readSync(fd, head, 0, 16, 0);
const headerSize = head.readUInt32LE(12);
const headerBuf = Buffer.alloc(headerSize);
fs.readSync(fd, headerBuf, 0, headerSize, 16);
const header = JSON.parse(headerBuf.toString('utf8'));
const base = 16 + headerSize;

function lookup(p) {
  const parts = p.split('/');
  let node = header;
  for (const part of parts) {
    if (!node.files || !node.files[part]) return null;
    node = node.files[part];
  }
  return node;
}

const outDir = 'D:\\PioneerFrameworkLite\\.tmp-mcp';
fs.mkdirSync(outDir, { recursive: true });
for (const t of process.argv.slice(2)) {
  const node = lookup(t);
  if (!node || !node.offset) {
    console.log('MISS', t);
    continue;
  }
  const size = Number(node.size);
  const offset = Number(node.offset) + base;
  const buf = Buffer.alloc(size);
  fs.readSync(fd, buf, 0, size, offset);
  const dest = path.join(outDir, path.basename(t));
  fs.writeFileSync(dest, buf);
  console.log('OK', t, '->', dest, size);
}
fs.closeSync(fd);
