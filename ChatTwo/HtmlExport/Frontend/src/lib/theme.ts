export type ThemeMode = 'system' | 'light' | 'dark';

export function applyThemePreference(mode: ThemeMode): void {
	if (mode === 'system') {
		delete document.documentElement.dataset.theme;
	} else {
		document.documentElement.dataset.theme = mode;
	}
}

