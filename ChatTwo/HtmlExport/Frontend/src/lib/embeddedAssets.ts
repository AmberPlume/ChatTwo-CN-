import type { EmbeddedAssets } from '$lib/embeddedExport';
import { decodeBase64ToBytes, decodeBase64ToText } from '$lib/embeddedExport';
import { applyEmbeddedFont, clearEmbeddedFont, getEmbeddedFontFamily } from '$lib/embeddedFont';
import { addGfdStylesheetFromBytes } from '$lib/embeddedGfd';

export type EmbeddedAssetStatus = {
	fontStatus: string;
	texStatus: string;
	gfdStatus: string;
	assetWarning: string | null;
};

export async function loadEmbeddedAssetStatus(
	embeddedAssets: EmbeddedAssets,
	pageNumberFormat: Intl.NumberFormat
): Promise<EmbeddedAssetStatus> {
	let fontStatus = 'Embedded font missing.';
	let texStatus = 'Embedded .tex missing.';
	let gfdStatus = 'Embedded .gfd missing.';
	let assetWarning: string | null = null;

	if (embeddedAssets.ttfBase64) {
		try {
			const ttfBytes = decodeBase64ToBytes(embeddedAssets.ttfBase64);
			const fontFamily = getEmbeddedFontFamily();
			applyEmbeddedFont(embeddedAssets.ttfBase64, fontFamily);
			fontStatus = `Loaded embedded font "${fontFamily}" (${pageNumberFormat.format(ttfBytes.length)} bytes).`;
		} catch {
			clearEmbeddedFont();
			fontStatus = 'Embedded font present but invalid.';
			assetWarning = 'Asset decode failed (ttf)';
		}
	} else {
		clearEmbeddedFont();
	}

	if (embeddedAssets.texBase64) {
		try {
			const texText = decodeBase64ToText(embeddedAssets.texBase64, embeddedAssets.encoding ?? 'utf-8');
			texStatus = `Loaded .tex asset (${pageNumberFormat.format(texText.length)} chars).`;
		} catch {
			texStatus = 'Embedded .tex present but invalid.';
			assetWarning = assetWarning ?? 'Asset decode failed (.tex)';
		}
	}

	if (embeddedAssets.gfdBase64) {
		try {
			const gfdBytes = decodeBase64ToBytes(embeddedAssets.gfdBase64);
			if (embeddedAssets.texBase64) {
				const texBytes = decodeBase64ToBytes(embeddedAssets.texBase64);
				const iconCount = await addGfdStylesheetFromBytes(gfdBytes, texBytes);
				gfdStatus = `Loaded .gfd asset (${pageNumberFormat.format(gfdBytes.length)} bytes, ${pageNumberFormat.format(iconCount)} entries).`;
			} else {
				gfdStatus = `Loaded .gfd asset (${pageNumberFormat.format(gfdBytes.length)} bytes).`;
			}
		} catch {
			gfdStatus = 'Embedded .gfd present but invalid.';
			assetWarning = assetWarning ?? 'Asset decode failed (.gfd)';
		}
	}

	return { fontStatus, texStatus, gfdStatus, assetWarning };
}

