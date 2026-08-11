import type { Action } from 'svelte/action';
import type { ChatTemplate } from './types';
import { processColor } from './chatColors';

export const templateColor: Action<HTMLSpanElement, ChatTemplate> = (node, template) => {
	processColor(template, node);
	return {
		update(next) {
			processColor(next, node);
		}
	};
};
