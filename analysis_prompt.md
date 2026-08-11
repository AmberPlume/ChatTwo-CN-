

You are debugging a text selection feature in an ImGui-based C# FFXIV Dalamud plugin. Read these files and analyze what is fundamentally broken with the text selection system:

FILE 1: Ui/ChatLog/ChatLog.Window.cs - look for class TextSelectionState and method DrawMessageLog
FILE 2: Util/ImGuiUtil.cs - look for WrapText method

USER REPORTS:
1. Dragging up selects entire above line regardless of distance
2. Partial line selection selects full line + previous line
3. Scrolling makes selection follow scroll instead of anchoring

CRITICAL CHECKS:
1. In WrapText: oldPos = ImGui.GetCursorScreenPos() is called BEFORE TextUnformatted. But what about Indent(14f)? Is the indent applied BEFORE AddChunk records coordinates?
2. PointToChar finds nearest chunk by centerY with scoring: in-range gets dy*0.5. If user drags slightly above row N, which row wins?
3. DrawHighlight draws inside same Child window as text. Chunk.Min.X should match — but does Indent(14f) in WrapText cause an X offset mismatch?

OUTPUT: List each bug with file path, line range, and explanation. Focus on Y-coordinate bugs first.

