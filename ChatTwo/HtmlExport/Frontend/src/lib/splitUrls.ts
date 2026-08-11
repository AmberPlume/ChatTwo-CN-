export type TextPiece = { type: 'text'; value: string } | { type: 'url'; href: string; label: string };

/** http(s) URLs; excludes trailing punctuation often glued to URLs in prose. */
const URL_RE = /https?:\/\/[^\s<>"'()[\]{}|\\^`]+/gi;

function trimTrailingPunct(url: string): string {
	let s = url;
	while (s.length > 0 && /[.,;:!?)\]}>]+$/.test(s.slice(-1))) {
		s = s.slice(0, -1);
	}
	return s;
}

export function splitTextWithUrls(text: string): TextPiece[] {
	if (!text) return [];
	const pieces: TextPiece[] = [];
	let lastIndex = 0;
	const re = new RegExp(URL_RE.source, 'gi');
	let m: RegExpExecArray | null;
	while ((m = re.exec(text)) !== null) {
		const raw = m[0];
		const href = trimTrailingPunct(raw);
		if (m.index > lastIndex) {
			pieces.push({ type: 'text', value: text.slice(lastIndex, m.index) });
		}
		if (href.length > 0) {
			pieces.push({ type: 'url', href, label: href });
		}
		lastIndex = m.index + raw.length;
	}
	if (lastIndex < text.length) {
		pieces.push({ type: 'text', value: text.slice(lastIndex) });
	}
	return pieces;
}
