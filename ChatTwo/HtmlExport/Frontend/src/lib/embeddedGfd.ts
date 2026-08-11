// based off https://github.com/Infiziert90/ChatTwo/blob/main/ChatTwo/Http/Frontend/src/lib/gfd.ts
interface GfdEntry {
	id: number;
	left: number;
	top: number;
	width: number;
	height: number;
	unk0A: number;
	redirect: number;
	unk0E: number;
}

interface StylesheetEntry {
	ids: number[];
	style1: string;
	style2: string;
	width: number;
}

interface ParsedTex {
	buffer: ArrayBuffer;
	type: number;
	format: number;
	width: number;
	height: number;
	depth: number;
	mipsAndFlag: number;
	arraySize: number;
	lodOffsets: number[];
	offsetToSurface: number[];
}

const GFD_STYLE_ID = 'chat2-gfd-style';

function parseTex(arrayBuffer: ArrayBuffer): ParsedTex {
	const buffer = new DataView(arrayBuffer);
	const type = buffer.getInt32(0, true);
	const format = buffer.getInt32(4, true);
	const width = buffer.getInt16(8, true);
	const height = buffer.getInt16(10, true);
	const depth = buffer.getInt16(12, true);
	const mipsAndFlag = buffer.getInt8(14);
	const arraySize = buffer.getInt8(15);
	const lodOffsets = [buffer.getInt32(16, true), buffer.getInt32(20, true), buffer.getInt32(24, true)];
	const offsetToSurface = [
		buffer.getInt32(28, true),
		buffer.getInt32(32, true),
		buffer.getInt32(36, true),
		buffer.getInt32(40, true),
		buffer.getInt32(44, true),
		buffer.getInt32(48, true),
		buffer.getInt32(52, true),
		buffer.getInt32(56, true),
		buffer.getInt32(60, true),
		buffer.getInt32(64, true),
		buffer.getInt32(68, true),
		buffer.getInt32(72, true),
		buffer.getInt32(76, true)
	];

	return {
		buffer: arrayBuffer,
		type,
		format,
		width,
		height,
		depth,
		mipsAndFlag,
		arraySize,
		lodOffsets,
		offsetToSurface
	};
}

function loadGfdFromBytes(bytes: Uint8Array): GfdEntry[] {
	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const count = view.getInt32(8, true);
	const entries: GfdEntry[] = new Array(count);
	for (let i = 0; i < count; i += 1) {
		const offset = 0x10 + i * 0x10;
		entries[i] = {
			id: view.getInt16(offset, true),
			left: view.getInt16(offset + 2, true),
			top: view.getInt16(offset + 4, true),
			width: view.getInt16(offset + 6, true),
			height: view.getInt16(offset + 8, true),
			unk0A: view.getInt16(offset + 10, true),
			redirect: view.getInt16(offset + 12, true),
			unk0E: view.getInt16(offset + 14, true)
		};
	}
	return entries;
}

async function texBytesToBlob(texBytes: Uint8Array): Promise<Blob> {
	const texArrayBuffer = texBytes.buffer.slice(
		texBytes.byteOffset,
		texBytes.byteOffset + texBytes.byteLength
	) as ArrayBuffer;
	const parsed = parseTex(texArrayBuffer);
	if (parsed.format !== 0x1450) throw new Error('Asset decode failed');

	const dataArray = new Uint8ClampedArray(
		parsed.buffer,
		parsed.offsetToSurface[0],
		parsed.width * parsed.height * 4
	);
	for (let i = 0; i < dataArray.length; i += 4) {
		const t = dataArray[i];
		dataArray[i] = dataArray[i + 2];
		dataArray[i + 2] = t;
	}

	const imageData = new ImageData(dataArray, parsed.width, parsed.height);
	const bitmap = await createImageBitmap(imageData);
	const canvas = new OffscreenCanvas(parsed.width, parsed.height);
	canvas.getContext('bitmaprenderer')?.transferFromImageBitmap(bitmap);
	return canvas.convertToBlob();
}

export async function addGfdStylesheetFromBytes(
	gfdBytes: Uint8Array,
	texBytes: Uint8Array
): Promise<number> {
	const texBlob = await texBytesToBlob(texBytes);
	const texUrl = URL.createObjectURL(texBlob);
	const gfd = loadGfdFromBytes(gfdBytes);

	const stylesheets: { [id: number]: StylesheetEntry } = [];
	for (const entry of gfd) {
		if (entry.width * entry.height <= 0) continue;

		if (entry.redirect !== 0) {
			const redirectTarget = stylesheets[entry.redirect];
			if (redirectTarget) redirectTarget.ids.push(entry.id);
			continue;
		}

		stylesheets[entry.id] = {
			ids: [entry.id],
			style1: [
				`background-position: -${entry.left}px -${entry.top}px`,
				`background-image: url('${texUrl}')`,
				`width: ${entry.width}px`,
				`height: ${entry.height}px`
			].join(';'),
			style2: [
				`background-position: -${entry.left * 2}px -${entry.top * 2 + 341}px`,
				`background-image: url('${texUrl}')`,
				`width: ${entry.width * 2}px`,
				`height: ${entry.height * 2}px`
			].join(';'),
			width: entry.width
		};
	}

	let stylesheet = '';
	for (const entry of Object.values(stylesheets)) {
		if (!entry) continue;
		stylesheet += `\n${entry.ids.map((x) => `.gfd-icon.gfd-icon-${x}::before`).join(', ')}{${entry.style1};}`;
		stylesheet += `\n${entry.ids.map((x) => `.gfd-icon.gfd-icon-hq-${x}::before`).join(', ')}{${entry.style2};}`;
		stylesheet += `\n${entry.ids.map((x) => `.gfd-icon.gfd-icon-${x}`).join(', ')}{width:${entry.width}px;}`;
		stylesheet += `\n${entry.ids.map((x) => `.gfd-icon.gfd-icon-hq-${x}`).join(', ')}{width:${entry.width * 2}px;}`;
	}

	const existing = document.getElementById(GFD_STYLE_ID);
	const styleNode = existing instanceof HTMLStyleElement ? existing : document.createElement('style');
	styleNode.id = GFD_STYLE_ID;
	styleNode.textContent = stylesheet;
	if (!existing) document.head.appendChild(styleNode);
	return gfd.length;
}

