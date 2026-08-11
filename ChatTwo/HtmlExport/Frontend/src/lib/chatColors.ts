import type { ChatTemplate } from './types';

function rgbaFromTemplate(template: ChatTemplate): string {
	const c = template.color >>> 0;
	const r = (c & 0xff000000) >>> 24;
	const g = (c & 0xff0000) >>> 16;
	const b = (c & 0xff00) >>> 8;
	const a = (c & 0xff) / 255.0;
	return `rgba(${r}, ${g}, ${b}, ${a})`;
}

export function processColor(template: ChatTemplate, spanElement: HTMLSpanElement): void {
	const parsedColor = rgbaFromTemplate(template);
	spanElement.style.setProperty('--parsed-color', parsedColor);
}