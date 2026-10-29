import { copyFile } from 'node:fs/promises';
await copyFile(new URL('./dist/index.html', import.meta.url), new URL('./standalone.html', import.meta.url));
console.log('Standalone page written to standalone.html');
