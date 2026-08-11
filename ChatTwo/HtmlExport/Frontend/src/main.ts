import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';

function init() {
	const el = document.getElementById('app');
	if (!el) throw new Error('#app not found');
	mount(App, { target: el });
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}
