<script lang="ts">
	import type { ChatExport, ChatMessage as ChatMessageModel } from '$lib/types';
	import {
		parseEmbeddedExportFromDocument
	} from '$lib/embeddedExport';
	import { clearEmbeddedFont } from '$lib/embeddedFont';
	import { loadEmbeddedAssetStatus } from '$lib/embeddedAssets';
	import { applyThemePreference, type ThemeMode } from '$lib/theme';
	import ChatMessage from './components/ChatMessage.svelte';
	import DebugPanel from './components/DebugPanel.svelte';
	import MessageCircle from '@lucide/svelte/icons/message-circle';
	import Hourglass from '@lucide/svelte/icons/hourglass';
	import ArrowRight from '@lucide/svelte/icons/arrow-right';
	import ArrowLeft from '@lucide/svelte/icons/arrow-left';
	import Search from '@lucide/svelte/icons/search';
	import X from '@lucide/svelte/icons/x';

	let allMessages = $state<ChatMessageModel[]>([]);
	let loadError = $state<string | null>(null);
	let loadedNames = $state<string[]>([]);
	let offset = $state(0);
	let limit = $state(1000);
	let searchDraft = $state('');
	let appliedSearch = $state('');
	let fileLoading = $state(false);
	let jsonFileName = $state<string | null>(null);
	let fontStatus = $state('No embedded font loaded.');
	let texStatus = $state('No .tex asset loaded.');
	let gfdStatus = $state('No .gfd asset loaded.');
	let assetWarning = $state<string | null>(null);
	let debugPanelOpen = $state(false);
	let themeMode = $state<ThemeMode>('system');

	const pageNumberFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

	const normalizedSearch = $derived(appliedSearch.trim().toLowerCase());

	function messagePlainText(msg: ChatMessageModel): string {
		return msg.templates
			.map((template) => {
				if (template.content) return template.content;
				if (template.iconId) return `[icon ${template.iconId}]`;
				return `[type ${template.payloadType}]`;
			})
			.join('');
	}

	/** Messages in the current pagination window only (not the whole log). */
	const pageWindow = $derived(allMessages.slice(offset, offset + limit));

	/** Search runs only inside `pageWindow`. */
	const pageMessages = $derived(
		normalizedSearch === ''
			? pageWindow
			: pageWindow.filter((m) => messagePlainText(m).toLowerCase().includes(normalizedSearch))
	);

	const hasMore = $derived(offset + limit < allMessages.length);

	const pageRangeText = $derived(
		`${pageNumberFormat.format(offset + 1)}–${pageNumberFormat.format(
			Math.min(offset + limit, allMessages.length)
		)} of ${pageNumberFormat.format(allMessages.length)}`
	);

	function prevPage() {
		offset = Math.max(0, offset - limit);
	}

	function nextPage() {
		if (hasMore) offset += limit;
	}

	function submitSearch(e?: Event) {
		e?.preventDefault();
		appliedSearch = searchDraft;
	}

	function clearSearch() {
		searchDraft = '';
		appliedSearch = '';
	}

	async function onFilePicked(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const files = Array.from(input.files ?? []);
		// Allow re-selecting the same file(s) and still firing `change`.
		input.value = '';
		if (files.length === 0) return;

		fileLoading = true;
		loadError = null;
		assetWarning = null;
		loadedNames = files.map((file) => file.name);
		offset = 0;
		searchDraft = '';
		appliedSearch = '';
		jsonFileName = null;

		try {
			const jsonFiles = files.filter((file) => file.name.toLowerCase().endsWith('.json'));
			if (jsonFiles.length === 0) throw new Error('JSON chat export missing');

			const jsonFile = jsonFiles[0];
			jsonFileName = jsonFile.name;
			const jsonText = await jsonFile.text();
			let chatExport: ChatExport;
			try {
				chatExport = JSON.parse(jsonText) as ChatExport;
			} catch {
				throw new Error('JSON chat export invalid');
			}
			allMessages = chatExport.messages ?? [];

			try {
				const embeddedAssets = parseEmbeddedExportFromDocument();
				const status = await loadEmbeddedAssetStatus(embeddedAssets, pageNumberFormat);
				fontStatus = status.fontStatus;
				texStatus = status.texStatus;
				gfdStatus = status.gfdStatus;
				assetWarning = status.assetWarning;
			} catch (assetErr) {
				clearEmbeddedFont();
				fontStatus = 'Embedded font missing.';
				texStatus = 'Embedded .tex missing.';
				gfdStatus = 'Embedded .gfd missing.';
				assetWarning = assetErr instanceof Error ? assetErr.message : String(assetErr);
			}
		} catch (err) {
			allMessages = [];
			loadedNames = [];
			clearEmbeddedFont();
			loadError = err instanceof Error ? err.message : String(err);
		} finally {
			fileLoading = false;
		}
	}

	$effect(() => {
		if (fileLoading) return;
		(async () => {
			try {
				const embeddedAssets = parseEmbeddedExportFromDocument();
				const status = await loadEmbeddedAssetStatus(embeddedAssets, pageNumberFormat);
				fontStatus = status.fontStatus;
				texStatus = status.texStatus;
				gfdStatus = status.gfdStatus;
				assetWarning = status.assetWarning;
			} catch (err) {
				clearEmbeddedFont();
				fontStatus = 'Embedded font missing.';
				texStatus = 'Embedded .tex missing.';
				gfdStatus = 'Embedded .gfd missing.';
				assetWarning = err instanceof Error ? err.message : String(err);
			}
		})();
	});

	$effect(() => {
		applyThemePreference(themeMode);
	});

	$effect(() => {
		const total = allMessages.length;
		if (total > 0) {
			// Guard against stale offsets after a new file load or smaller result set.
			const maxOffset = Math.max(0, total - limit);
			if (offset > maxOffset) offset = maxOffset;
		} else if (offset !== 0) {
			offset = 0;
		}
	});
</script>

{#if fileLoading}
	<div class="loading-screen" role="status" aria-live="polite">
		<Hourglass size={32} strokeWidth={3} />
		<p>Loading files… <br> <small>Reading JSON and embedded HTML assets.</small></p>
	</div>
{/if}

<div class="app-top-bar">
	<header>
		<h1>
			<MessageCircle size={16} strokeWidth={3} />
			Chat2 viewer
		</h1>
		<label class="top-bar__file">
			<span class="top-bar__file-name">{jsonFileName ? `JSON: ${jsonFileName}` : 'JSON: not loaded'}</span>
			<input
				type="file"
				multiple
				accept=".json,application/json"
				onchange={onFilePicked}
				disabled={fileLoading}
			/>
		</label>
		<DebugPanel
			bind:open={debugPanelOpen}
			bind:themeMode
			{fontStatus}
			{texStatus}
			{gfdStatus}
			{assetWarning}
		/>
	</header>

	{#if allMessages.length > 0}
		<div id="viewer-controls">
			<div role="toolbar" aria-label="Pagination">
				<button
					type="button"
					onclick={prevPage}
					disabled={fileLoading || offset === 0}
					aria-label="Previous page"
				>
					<ArrowLeft aria-hidden="true" />
				</button>
				<p>{pageRangeText}</p>
				<button
					type="button"
					class="bottom-dock__nav-btn"
					onclick={nextPage}
					disabled={fileLoading || !hasMore}
					aria-label="Next page"
				>
					<ArrowRight aria-hidden="true" />
				</button>
			</div>
			<form onsubmit={submitSearch}>
				<label class="field-label">
					<span>Search this page</span>
					<input
						type="search"
						name="q"
						placeholder="Filter current page…"
						bind:value={searchDraft}
						disabled={fileLoading}
						autocomplete="off"
					/>
				</label>
				<button type="submit" disabled={fileLoading} aria-label="Apply search">
					<Search aria-hidden="true" />
				</button>
				<button type="button" onclick={clearSearch} disabled={fileLoading} aria-label="Clear search">
					<X aria-hidden="true" />
				</button>
			</form>
		</div>
	{/if}
</div>

<main class="chat-viewer__main">
{#if loadError}
	<p class="notice notice--error" role="alert">Error: {loadError}</p>
{:else if loadedNames.length > 0 && allMessages.length === 0 && !fileLoading}
	<p class="notice">
		No messages in {loadedNames.length === 1 ? `file: ${loadedNames[0]}` : `${loadedNames.length} files`}.
	</p>
{:else if allMessages.length === 0 && !fileLoading}
	<p class="empty-state">Choose a JSON export to view.</p>
{:else if allMessages.length > 0}
	<ul class="message-list">
	<!-- Offset is included to prevent key collisions across pages/files with reused message ids. -->
		{#each pageMessages as message, index (`${message.id}-${offset + index}`)}
			<ChatMessage {message} />
		{/each}
	</ul>
{/if}
</main>
