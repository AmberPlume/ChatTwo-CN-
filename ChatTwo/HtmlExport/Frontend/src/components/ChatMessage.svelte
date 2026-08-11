<script lang="ts">
	import type { ChatMessage } from '$lib/types';
	import { templateColor } from '$lib/templateColor';
	import { splitTextWithUrls } from '$lib/splitUrls';

	interface Props {
		message: ChatMessage;
	}

	let { message }: Props = $props();
</script>

<li class="chat-message">
	<strong class="timestamp">{message.timestamp}</strong>
	<span class="body">
		{#each message.templates as template, templateIndex (`${message.id}-${templateIndex}`)}
			{#if template.content}
				<span class="template-segment" use:templateColor={template}>
					<!-- URLs are split at render time so links stay clickable while plain text remains untouched. -->
					{#each splitTextWithUrls(template.content) as piece, pieceIndex (`${templateIndex}-${pieceIndex}`)}
						{#if piece.type === 'url'}
							<a
								class="template-link"
								href={piece.href}
								target="_blank"
								rel="noopener noreferrer">{piece.label}</a>
						{:else}
							{piece.value}
						{/if}
					{/each}
				</span>
			{:else if template.iconId}
				<span
					class={`template-icon gfd-icon gfd-icon-${template.iconId}`}
					aria-label={`icon ${template.iconId}`}
					use:templateColor={template}
				></span>
			{:else}
				<span class="template-segment" use:templateColor={template}>[type {template.payloadType}]</span>
			{/if}
		{/each}
	</span>
</li>
