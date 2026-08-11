export type EmbeddedAssets = {
	ttfBase64?: string;
	texBase64?: string;
	gfdBase64?: string;
	encoding?: string;
};

export type EmbeddedExportShape = {
	assets?: EmbeddedAssets;
};

const EMBEDDED_EXPORT_SELECTOR = 'script#chat2-embedded-export[type="application/json"]';

function asObject(value: unknown): Record<string, unknown> {
	// Keep JSON parsing strict so we fail early on malformed payloads.
	if (!value || typeof value !== 'object' || Array.isArray(value)) {
		throw new Error('Invalid embedded asset JSON');
	}
	return value as Record<string, unknown>;
}

function asOptionalString(value: unknown, fieldName: string): string | undefined {
	if (value == null) return undefined;
	if (typeof value !== 'string') {
		throw new Error(`Invalid embedded asset JSON: "${fieldName}" must be a string`);
	}
	return value;
}

function parseEmbeddedExportPayload(payloadText: string): EmbeddedAssets {
	let parsed: EmbeddedExportShape;
	try {
		parsed = JSON.parse(payloadText) as EmbeddedExportShape;
	} catch {
		throw new Error('Invalid embedded asset JSON');
	}

	// Validate and narrow each field to optional strings before returning.
	const root = asObject(parsed);
	const assetsObj = asObject(root.assets);
	return {
		ttfBase64: asOptionalString(assetsObj.ttfBase64, 'ttfBase64'),
		texBase64: asOptionalString(assetsObj.texBase64, 'texBase64'),
		gfdBase64: asOptionalString(assetsObj.gfdBase64, 'gfdBase64'),
		encoding: asOptionalString(assetsObj.encoding, 'encoding')
	};
}

export function parseEmbeddedExportFromHtml(html: string): EmbeddedAssets {
	// Used when importing from raw HTML text (e.g. dropped files / pasted markup).
	const doc = new DOMParser().parseFromString(html, 'text/html');
	const script = doc.querySelector(EMBEDDED_EXPORT_SELECTOR);
	if (!script) throw new Error('Embedded export block not found');
	const payloadText = script.textContent?.trim();
	if (!payloadText) throw new Error('Invalid embedded asset JSON');
	return parseEmbeddedExportPayload(payloadText);
}

export function parseEmbeddedExportFromDocument(doc: Document = document): EmbeddedAssets {
	// Used when reading embedded assets from the current live document.
	const script = doc.querySelector(EMBEDDED_EXPORT_SELECTOR);
	if (!script) throw new Error('Embedded export block not found');
	const payloadText = script.textContent?.trim();
	if (!payloadText) throw new Error('Invalid embedded asset JSON');
	return parseEmbeddedExportPayload(payloadText);
}

function normalizeBase64(base64: string): string {
	// Some payloads include wrapped base64 with whitespace/newlines.
	return base64.replace(/\s+/g, '');
}

export function decodeBase64ToBytes(base64: string): Uint8Array {
	const normalized = normalizeBase64(base64);
	try {
		const binary = atob(normalized);
		// Convert the binary string from atob into a byte array.
		const bytes = new Uint8Array(binary.length);
		for (let i = 0; i < binary.length; i += 1) {
			bytes[i] = binary.charCodeAt(i);
		}
		return bytes;
	} catch {
		throw new Error('Asset decode failed');
	}
}

export function decodeBase64ToText(base64: string, encoding = 'utf-8'): string {
	try {
		const bytes = decodeBase64ToBytes(base64);
		return new TextDecoder(encoding).decode(bytes);
	} catch {
		throw new Error('Asset decode failed');
	}
}

