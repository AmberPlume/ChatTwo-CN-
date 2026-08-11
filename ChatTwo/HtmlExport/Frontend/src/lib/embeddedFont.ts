const EMBEDDED_FONT_STYLE_ID = 'chat2-embedded-font-style';
const DEFAULT_EMBEDDED_FONT_FAMILY = 'Chat2EmbeddedFont';
const DEFAULT_FONT_STACK =
	'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Oxygen, Ubuntu, Cantarell, "Open Sans", "Helvetica Neue", sans-serif';

export function clearEmbeddedFont(): void {
	document.getElementById(EMBEDDED_FONT_STYLE_ID)?.remove();
}

export function getEmbeddedFontFamily(fontFamily?: string): string {
	return fontFamily?.trim() || DEFAULT_EMBEDDED_FONT_FAMILY;
}

export function applyEmbeddedFont(base64: string, fontFamily: string): void {
	const existing = document.getElementById(EMBEDDED_FONT_STYLE_ID);
	const styleEl = existing instanceof HTMLStyleElement ? existing : document.createElement('style');
	styleEl.id = EMBEDDED_FONT_STYLE_ID;
	styleEl.textContent = `
@font-face {
	font-family: "${fontFamily}";
	src: url("data:font/ttf;base64,${base64}") format("truetype");
	font-display: swap;
}
html {
	font-family: "${fontFamily}", ${DEFAULT_FONT_STACK};
}
:root {
	--chat-message-font-family: "${fontFamily}", ${DEFAULT_FONT_STACK};
}
`;
	if (!existing) {
		document.head.appendChild(styleEl);
	}
}

