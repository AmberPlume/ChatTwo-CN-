<script lang="ts">
	import Bug from '@lucide/svelte/icons/bug';
	import X from '@lucide/svelte/icons/x';
	import type { ThemeMode } from '$lib/theme';

	interface Props {
		open: boolean;
		themeMode: ThemeMode;
		fontStatus: string;
		texStatus: string;
		gfdStatus: string;
		assetWarning: string | null;
	}

	let {
		open = $bindable(),
		themeMode = $bindable(),
		fontStatus,
		texStatus,
		gfdStatus,
		assetWarning
	}: Props = $props();

	let dialog: HTMLDialogElement | null = null;

	function togglePanel() {
		open = !open;
	}

	function closePanel() {
		open = false;
	}

	function handleDialogClose() {
		open = false;
	}

	$effect(() => {
		if (!dialog) return;

		if (open && !dialog.open) {
			dialog.showModal();
		} else if (!open && dialog.open) {
			dialog.close();
		}
	});
</script>

<div class="top-bar__debug">
	<button
		type="button"
		class="top-bar__debug-btn"
		aria-label="Open debug panel"
		aria-expanded={open}
		onclick={togglePanel}
	>
		<Bug aria-hidden="true" />
	</button>

	<dialog
		id="viewer-debug-panel"
		bind:this={dialog}
		aria-label="Debug settings"
		onclose={handleDialogClose}
	>
		<header>
			<h2>Debug Panel</h2>
			<button
				type="button"
				class="debug-panel__close-btn"
				aria-label="Close debug panel"
				onclick={closePanel}
			>
				<X aria-hidden="true" />
			</button>
		</header>
		<p>{fontStatus}</p>
		<p>{texStatus}</p>
		<p>{gfdStatus}</p>
		{#if assetWarning}
			<p class="notice notice--error" role="status">{assetWarning}</p>
		{/if}
		<label class="field-label">
			<span>Theme</span>
			<select bind:value={themeMode}>
				<option value="system">System</option>
				<option value="light">Light</option>
				<option value="dark">Dark</option>
			</select>
		</label>
	</dialog>
</div>

