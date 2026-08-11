import path from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vite';
import { viteSingleFile } from 'vite-plugin-singlefile';

export default defineConfig({
	plugins: [svelte(), viteSingleFile()],
	base: './',
	resolve: {
		alias: {
			$lib: path.resolve(__dirname, 'src/lib')
		}
	},
	build: {
		outDir: 'build',
		emptyOutDir: true,
		modulePreload: false,
		rollupOptions: {
			input: path.resolve(__dirname, 'index.html')
		}
	}
});
