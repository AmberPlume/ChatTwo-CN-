export type ChatTemplate = {
	payloadType: number;
	content: string;
	iconId: number;
	color: number;
};

export type ChatMessage = {
	id: string;
	timestamp: string;
	templates: ChatTemplate[];
};

export type ChatExport = {
	messages: ChatMessage[];
};
