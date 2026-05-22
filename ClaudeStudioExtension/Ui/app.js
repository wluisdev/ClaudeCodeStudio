console.log("Claude Code Studio loaded");

// ── Layout toggle ────────────────────────────────────────────
function toggleLayout() {
    const app = document.querySelector(".app");
    const btn = document.getElementById("btn-layout");
    app.classList.toggle("compact");
    const compact = app.classList.contains("compact");
    btn.classList.toggle("active", compact);
    localStorage.setItem("compactLayout", compact ? "1" : "0");
}

(function restoreCompactLayout() {
    if (localStorage.getItem("compactLayout") === "1") {
        const app = document.querySelector(".app");
        const btn = document.getElementById("btn-layout");
        app.classList.add("compact");
        if (btn) btn.classList.add("active");
    }
})();

// ── Composer splitter ────────────────────────────────────────
(function initSplitter() {
    const splitter = document.getElementById("splitter");
    if (!splitter) return;

    const MIN_HEIGHT = 54;
    const MAX_HEIGHT = 400;
    const ta = document.querySelector(".composer textarea");

    const saved = parseInt(localStorage.getItem("composerTextareaHeight") || "0", 10);
    if (saved >= MIN_HEIGHT && saved <= MAX_HEIGHT && ta) {
        ta.style.height = saved + "px";
    }

    let startY = 0;
    let startHeight = 0;

    function onMove(e) {
        const delta = startY - e.clientY;
        const next = Math.max(MIN_HEIGHT, Math.min(MAX_HEIGHT, startHeight + delta));
        ta.style.height = next + "px";
    }

    function onUp() {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        splitter.classList.remove("dragging");
        document.body.classList.remove("splitter-dragging");
        const h = parseInt(ta.style.height, 10);
        if (h) localStorage.setItem("composerTextareaHeight", String(h));
    }

    splitter.addEventListener("mousedown", e => {
        if (!ta) return;
        e.preventDefault();
        startY = e.clientY;
        startHeight = ta.getBoundingClientRect().height;
        splitter.classList.add("dragging");
        document.body.classList.add("splitter-dragging");
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
    });
})();

// ── Permission mode cycle ─────────────────────────────────────
const permissionCycle = ["ask", "plan", "yolo"];
const permissionIcons = { yolo: "⚡", plan: "📋", ask: "🔒" };
const permissionLabels = { yolo: "yolo — skips ALL permission prompts (dangerous)", plan: "plan — plans only", ask: "ask — Claude default" };

function cyclePermission() {
    const cur = permissionSelect.value;
    const next = permissionCycle[(permissionCycle.indexOf(cur) + 1) % permissionCycle.length];
    permissionSelect.value = next;
    updatePermissionBtn();
}

function updatePermissionBtn() {
    const btn = document.getElementById("btn-permission");
    const mode = permissionSelect.value;
    btn.textContent = permissionIcons[mode] || "🔒";
    btn.title = permissionLabels[mode] || mode;
    btn.classList.toggle("active", mode !== "yolo");
}


// ── Settings panel ───────────────────────────────────────────
function toggleSettings() {
    const menu = document.getElementById("settings-menu");
    menu.classList.toggle("open");
}

document.addEventListener("click", e => {
    const wrap = document.getElementById("settings-wrap");
    if (wrap && !wrap.contains(e.target))
        document.getElementById("settings-menu")?.classList.remove("open");
});

// ── Prompt history (Ctrl+Up / Ctrl+Down) ─────────────────────
let promptHistory = JSON.parse(localStorage.getItem("promptHistory") || "[]");
let historyIndex = -1;
let historyDraft = "";

function pushPromptHistory(text) {
    if (!text) return;
    if (promptHistory[0] === text) return;
    promptHistory.unshift(text);
    if (promptHistory.length > 50) promptHistory.pop();
    localStorage.setItem("promptHistory", JSON.stringify(promptHistory));
    historyIndex = -1;
}

// ── Session history ───────────────────────────────────────────
function toggleHistory() {
    const menu = document.getElementById("history-menu");
    const isOpen = menu.classList.toggle("open");
    if (isOpen) {
        document.getElementById("history-list").innerHTML =
            '<div class="cmd-item" style="color:#555;font-family:inherit">Loading…</div>';
        window.chrome.webview.postMessage({ type: "get-history" });
    }
}

document.addEventListener("click", e => {
    const wrap = document.getElementById("history-wrap");
    if (wrap && !wrap.contains(e.target))
        document.getElementById("history-menu")?.classList.remove("open");
});

// ── Custom commands ──────────────────────────────────────────
let customCommands = JSON.parse(localStorage.getItem("customCommands") || "[]");

function saveCustomCommands() {
    localStorage.setItem("customCommands", JSON.stringify(customCommands));
}

function renderCustomCommands() {
    const list = document.getElementById("custom-cmd-list");
    list.innerHTML = "";
    customCommands.forEach((cmd, i) => {
        const row = document.createElement("div");
        row.className = "cmd-item cmd-custom-item";
        row.innerHTML = `<span onclick="runCommand('${escapeAttr(cmd.command)}')">${escapeHtml(cmd.name || cmd.command)}</span><button class="cmd-custom-remove" onclick="removeCommand(${i})" title="Remove">×</button>`;
        list.appendChild(row);
    });
}

function removeCommand(i) {
    customCommands.splice(i, 1);
    saveCustomCommands();
    renderCustomCommands();
}

function toggleCmdMenu() {
    const menu = document.getElementById("cmd-menu");
    menu.classList.toggle("open");
}

function exportChatToMarkdown() {
    document.getElementById("cmd-menu").classList.remove("open");
    const messagesEl = document.getElementById("messages");
    const items = messagesEl.querySelectorAll(".message");
    if (items.length === 0) {
        window.chrome.webview.postMessage({ type: "export-markdown", content: "", empty: true });
        return;
    }

    const modelLabel = modelSelect.options[modelSelect.selectedIndex]?.text || modelSelect.value;
    const now = new Date();
    const pad = n => String(n).padStart(2, "0");
    const ts = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}`;

    let md = `# Claude Code Studio — Chat Export\n\n_Exported ${ts}_\n_Model: ${modelLabel}_\n\n---\n\n`;
    let lastRole = null;
    for (const msg of items) {
        const bubble = msg.querySelector(".bubble");
        if (!bubble) continue;
        const isUser = msg.classList.contains("user");
        const isAssistant = msg.classList.contains("assistant");
        if (!isUser && !isAssistant) continue;
        const role = isUser ? "You" : "Claude";
        const raw = bubble.dataset.raw;
        let text = (raw && raw.length > 0) ? raw : (bubble.textContent || "").trim();
        if (!text) continue;
        if (lastRole !== null) md += "\n---\n\n";
        md += `## ${role}\n\n${text.trim()}\n\n`;
        lastRole = role;
    }

    window.chrome.webview.postMessage({ type: "export-markdown", content: md });
}

let isUsageCapture = false;
let usageBuffer = "";
let sessionIn = 0;
let sessionOut = 0;

function runCommand(cmd) {
    document.getElementById("cmd-menu").classList.remove("open");
    if (cmd === "/usage") {
        openUsageModal();
        isUsageCapture = true;
        usageBuffer = "";
        document.getElementById("usage-raw").innerHTML = '<span class="usage-loading">Running /usage…</span>';
        textarea.value = cmd;
        sendMessage();
        return;
    }
    textarea.value = cmd;
    sendMessage();
}

function openUsageModal() {
    document.getElementById("usage-modal-overlay").classList.add("open");
    updateUsageSessionValues();
}

function closeUsageModal() {
    document.getElementById("usage-modal-overlay").classList.remove("open");
}

function updateUsageSessionValues() {
    const fmt = n => n >= 1000 ? `${(n / 1000).toFixed(1)}k` : `${n}`;
    const el = document.getElementById("usage-session-values");
    if (sessionIn === 0 && sessionOut === 0) { el.textContent = "—"; return; }
    el.textContent = `↑ ${fmt(sessionIn)} in · ↓ ${fmt(sessionOut)} out`;
}

function openAddCommand() {
    document.getElementById("cmd-menu").classList.remove("open");
    document.getElementById("cmd-modal-overlay").classList.add("open");
    document.getElementById("cmd-modal-name").value = "";
    document.getElementById("cmd-modal-cmd").value = "";
    setTimeout(() => document.getElementById("cmd-modal-name").focus(), 50);
}

function closeAddCommand() {
    document.getElementById("cmd-modal-overlay").classList.remove("open");
}

function saveNewCommand() {
    const name = document.getElementById("cmd-modal-name").value.trim();
    const cmd = document.getElementById("cmd-modal-cmd").value.trim();
    if (!cmd) return;
    customCommands.push({ name: name || cmd, command: cmd });
    saveCustomCommands();
    renderCustomCommands();
    closeAddCommand();
}

function escapeAttr(s) {
    return s.replace(/'/g, "\\'");
}

document.addEventListener("click", e => {
    const wrap = document.getElementById("cmd-wrap");
    if (wrap && !wrap.contains(e.target))
        document.getElementById("cmd-menu").classList.remove("open");
});
// ────────────────────────────────────────────────────────────

const textarea = document.querySelector("textarea");
const sendButton = null; // replaced by btnSend with streaming toggle
const newChatButton = document.querySelector(".new-chat");
const modelSelect = document.querySelector(".model-select");

function updateCaption() {
    const label = modelSelect.options[modelSelect.selectedIndex]?.text || modelSelect.value;
    try {
        window.chrome.webview.postMessage({ type: "set-caption", text: `Claude Code Studio — ${label}` });
    } catch (e) { /* webview not ready yet */ }
}
modelSelect.addEventListener("change", updateCaption);
window.addEventListener("DOMContentLoaded", updateCaption);
updateCaption();
const effortSlider = document.getElementById("effort-slider");
const effortLabel = document.getElementById("effort-label");
const effortValues = ["", "low", "medium", "high", "max"];
const effortLabels = ["auto", "low", "med", "high", "max"];
const effortSelect = { get value() { return effortValues[+effortSlider.value]; } };
(function () {
    const saved = localStorage.getItem("effortLevel") || "0";
    effortSlider.value = saved;
    effortLabel.textContent = effortLabels[+saved];
})();
const autoSaveSlider = document.getElementById("autosave-slider");
const autoSaveLabel = document.getElementById("autosave-label");
const autoSaveValues = ["none", "active", "all"];
(function () {
    const saved = localStorage.getItem("autoSaveLevel") ?? "1";
    autoSaveSlider.value = saved;
    autoSaveLabel.textContent = autoSaveValues[+saved];
})();
autoSaveSlider.addEventListener("input", () => {
    const i = +autoSaveSlider.value;
    autoSaveLabel.textContent = autoSaveValues[i];
    localStorage.setItem("autoSaveLevel", i);
});

effortSlider.addEventListener("input", () => {
    const i = +effortSlider.value;
    effortLabel.textContent = effortLabels[i];
    localStorage.setItem("effortLevel", i);
});
const permissionSelect = document.querySelector(".permission-select");
permissionSelect.addEventListener("change", updatePermissionBtn);
updatePermissionBtn();
const timingSelect = document.getElementById("timing-select");
(function () {
    const saved = localStorage.getItem("timingMode") || "simple";
    timingSelect.value = saved;
    document.querySelectorAll(".timing-opt").forEach(el => {
        if (el.dataset.value === saved) el.classList.add("timing-opt-active");
    });
})();

function setTimingMode(el) {
    timingSelect.value = el.dataset.value;
    localStorage.setItem("timingMode", el.dataset.value);
    document.querySelectorAll(".timing-opt").forEach(o => o.classList.remove("timing-opt-active"));
    el.classList.add("timing-opt-active");
}
const messages = document.querySelector("#messages");
const attachmentsEl = document.getElementById("attachments");
let welcome = document.querySelector(".welcome");

let _userScrolledUp = false;
const btnScrollBottom = document.getElementById("btn-scroll-bottom");

messages.addEventListener("scroll", () => {
    _userScrolledUp = messages.scrollTop + messages.clientHeight < messages.scrollHeight - 24;
    btnScrollBottom.classList.toggle("visible", _userScrolledUp);
});

messages.addEventListener("click", e => {
    const btn = e.target.closest(".apply-btn");
    if (!btn) return;
    const pre = btn.closest("pre");
    const codeEl = pre?.querySelector("code");
    if (!codeEl) return;
    const code = codeEl.innerText.replace(/\r\n/g, "\n");
    const langMatch = (codeEl.className || "").match(/language-([\w-]+)/);
    const language = langMatch ? langMatch[1] : "";
    window.chrome.webview.postMessage({ type: "apply-to-editor", code, language });
    const orig = btn.textContent;
    btn.textContent = "Applied";
    btn.classList.add("applied");
    setTimeout(() => { btn.textContent = orig; btn.classList.remove("applied"); }, 1200);
});

function scrollToBottom() {
    _userScrolledUp = false;
    btnScrollBottom.classList.remove("visible");
    messages.scrollTop = messages.scrollHeight;
}

let _toastTimer = null;
function showToast(text) {
    if (!text) return;
    let el = document.getElementById("toast");
    if (!el) {
        el = document.createElement("div");
        el.id = "toast";
        el.className = "toast";
        document.body.appendChild(el);
    }
    el.textContent = text;
    el.classList.add("visible");
    if (_toastTimer) clearTimeout(_toastTimer);
    _toastTimer = setTimeout(() => el.classList.remove("visible"), 2400);
}

function autoScroll() {
    if (!_userScrolledUp) messages.scrollTop = messages.scrollHeight;
}

let attachments = new Map();
let attachmentIdCounter = 0;
let msgCounter = 0;
let currentSessionId = null;

function decorateMessage(msgEl) {
    const idx = msgCounter++;
    msgEl.dataset.msgIndex = idx;
    const btn = document.createElement("button");
    btn.className = "msg-branch";
    btn.title = "Branch from here";
    btn.textContent = "⎇";
    btn.addEventListener("click", () => branchFromMessage(idx));
    msgEl.appendChild(btn);
}

function branchFromMessage(idx) {
    if (!currentSessionId) return;
    window.chrome.webview.postMessage({ type: "branch", msgIndex: idx });
}
let imageCounter = 0;
let currentStreamBubble = null;
let isStreaming = false;
const btnSend = document.getElementById("btn-send");

function setStreaming(on) {
    isStreaming = on;
    btnSend.textContent = on ? "■" : "↑";
    btnSend.classList.toggle("stop", on);
}

btnSend.addEventListener("click", () => {
    if (isStreaming) {
        window.chrome.webview.postMessage({ type: "cancel" });
        setStreaming(false);
        removeLoading();
        removeLiveTimer();
        if (currentStreamBubble) {
            finalizeBubbleStream(currentStreamBubble);
            const cancelled = document.createElement("span");
            cancelled.className = "cancelled";
            cancelled.textContent = "⊘ cancelled";
            currentStreamBubble.appendChild(cancelled);
            currentStreamBubble = null;
        } else {
            const msg = document.createElement("div");
            msg.className = "message assistant";
            msg.innerHTML = '<div class="bubble"><span class="cancelled">⊘ cancelled</span></div>';
            messages.appendChild(msg);
            autoScroll();
        }
    } else {
        sendMessage();
    }
});

// send handled by btnSend listener above
newChatButton.addEventListener("click", clearChat);

document.getElementById("btn-paste").addEventListener("click", async () => {
    window.chrome.webview.postMessage({ type: "get-clipboard-files" });
});

document.getElementById("btn-file").addEventListener("click", () => {
    window.chrome.webview.postMessage({ type: "add-file" });
});

document.getElementById("btn-selection").addEventListener("click", () => {
    window.chrome.webview.postMessage({ type: "get-selection" });
});

// ── Command autocomplete ──────────────────────────────────────
const autocompleteEl = document.getElementById("cmd-autocomplete");
let acIndex = -1;

const builtinCommands = ["/model", "/usage", "/compact", "/review", "/clear"];

function getMatchingCommands(query) {
    const all = [...builtinCommands, ...customCommands.map(c => c.command)];
    return query === "/" ? all : all.filter(c => c.startsWith(query));
}

function showAutocomplete(query) {
    const cmds = getMatchingCommands(query);
    if (cmds.length === 0) { hideAutocomplete(); return; }
    acIndex = -1;
    autocompleteEl.innerHTML = cmds.map(c =>
        `<div class="cmd-autocomplete-item" data-cmd="${escapeAttr(c)}">${escapeHtml(c)}</div>`
    ).join("");
    autocompleteEl.querySelectorAll(".cmd-autocomplete-item").forEach(el =>
        el.addEventListener("mousedown", e => { e.preventDefault(); fillAutocomplete(el.dataset.cmd); })
    );
    autocompleteEl.classList.add("open");
}

function hideAutocomplete() {
    autocompleteEl.classList.remove("open");
    acIndex = -1;
}

function fillAutocomplete(cmd) {
    textarea.value = cmd;
    hideAutocomplete();
    textarea.focus();
}

textarea.addEventListener("input", () => {
    const val = textarea.value;
    if (val.startsWith("/") && !val.includes(" ")) {
        showAutocomplete(val);
    } else {
        hideAutocomplete();
    }
    updateTokenEstimate();
});

textarea.addEventListener("keydown", (e) => {
    const isOpen = autocompleteEl.classList.contains("open");
    const items = autocompleteEl.querySelectorAll(".cmd-autocomplete-item");

    if (isOpen) {
        if (e.key === "ArrowDown") {
            e.preventDefault();
            acIndex = Math.min(acIndex + 1, items.length - 1);
            items.forEach((el, i) => el.classList.toggle("ac-selected", i === acIndex));
            return;
        }
        if (e.key === "ArrowUp") {
            e.preventDefault();
            acIndex = Math.max(acIndex - 1, 0);
            items.forEach((el, i) => el.classList.toggle("ac-selected", i === acIndex));
            return;
        }
        if (e.key === "Tab") {
            e.preventDefault();
            const el = items[acIndex >= 0 ? acIndex : 0];
            if (el) fillAutocomplete(el.dataset.cmd);
            return;
        }
        if (e.key === "Enter" && acIndex >= 0) {
            e.preventDefault();
            fillAutocomplete(items[acIndex].dataset.cmd);
            return;
        }
        if (e.key === "Escape") {
            hideAutocomplete();
            return;
        }
    }

    if (e.key === "Escape") {
        textarea.blur();
        window.chrome.webview.postMessage({ type: "unfocus" });
        return;
    }

    if (e.key === "ArrowUp" && e.ctrlKey && !isOpen) {
        e.preventDefault();
        if (historyIndex === -1) historyDraft = textarea.value;
        if (historyIndex < promptHistory.length - 1) {
            historyIndex++;
            textarea.value = promptHistory[historyIndex];
            updateTokenEstimate();
        }
        return;
    }
    if (e.key === "ArrowDown" && e.ctrlKey && !isOpen) {
        e.preventDefault();
        if (historyIndex > 0) {
            historyIndex--;
            textarea.value = promptHistory[historyIndex];
        } else if (historyIndex === 0) {
            historyIndex = -1;
            textarea.value = historyDraft;
        }
        updateTokenEstimate();
        return;
    }

    if (e.key === "Enter") {
        if (e.shiftKey) {
            if (sendEnterToggle.checked) return; // Shift+Enter = newline when send-on-enter is on
            e.preventDefault();
            sendMessage();
            return;
        }
        if (!sendEnterToggle.checked) return; // Enter = newline when send-on-enter is off
        e.preventDefault();
        hideAutocomplete();
        sendMessage();
    }

    if (e.ctrlKey && (e.key === "l" || e.key === "L")) {
        e.preventDefault();
        clearChat();
    }
});

// ── /model picker ─────────────────────────────────────────────
const modelList = [
    { id: "claude-sonnet-4-6",         label: "Sonnet 4.6" },
    { id: "claude-opus-4-7",           label: "Opus 4.7" },
    { id: "claude-haiku-4-5-20251001", label: "Haiku 4.5" },
];

function showModelPicker() {
    if (welcome) { welcome.remove(); welcome = null; }
    const current = modelSelect.value;
    const card = document.createElement("div");
    card.className = "question-card";
    card.innerHTML = `<div class="question-text">🤖 Choose the model:</div>
        <div class="question-buttons" style="flex-wrap:wrap;gap:6px">
        ${modelList.map(m =>
            `<button class="q-btn${m.id === current ? " q-yes" : ""}" onclick="selectModel('${m.id}',this.closest('.question-card'))">${escapeHtml(m.label)}</button>`
        ).join("")}
        </div>`;
    messages.appendChild(card);
    autoScroll();
}

function selectModel(id, card) {
    modelSelect.value = id;
    const label = modelSelect.options[modelSelect.selectedIndex]?.text || id;
    card.innerHTML = `<div class="question-text">🤖 Model: <strong>${escapeHtml(label)}</strong></div>`;
    card.classList.add("question-answered");
    updateCaption();
}

// ── Drag & drop ───────────────────────────────────────────────
const binaryExts = new Set(["png","jpg","jpeg","gif","bmp","ico","webp","tiff",
    "pdf","zip","rar","7z","tar","gz","exe","dll","bin","dat","pdb","mp3","mp4","wav","avi","mov"]);

const composerEl = document.querySelector(".composer");

composerEl.addEventListener("dragover", e => {
    e.preventDefault();
    composerEl.classList.add("drag-over");
});

composerEl.addEventListener("dragleave", e => {
    if (!composerEl.contains(e.relatedTarget))
        composerEl.classList.remove("drag-over");
});

composerEl.addEventListener("drop", e => {
    e.preventDefault();
    composerEl.classList.remove("drag-over");
    for (const file of e.dataTransfer.files) handleDroppedFile(file);
});

function handleDroppedFile(file) {
    const ext = file.name.split(".").pop().toLowerCase();
    if (binaryExts.has(ext)) {
        const reader = new FileReader();
        reader.onload = ev => {
            const base64 = ev.target.result.split(",")[1];
            window.chrome.webview.postMessage({ type: "save-dropped-file", filename: file.name, data: base64 });
        };
        reader.readAsDataURL(file);
    } else {
        const reader = new FileReader();
        reader.onload = ev => {
            const content = `[${file.name}]\n\`\`\`\n${ev.target.result}\n\`\`\``;
            addAttachment(file.name, content, false, null);
        };
        reader.readAsText(file);
    }
}

// ── Clipboard paste (Ctrl+V) ─────────────────────────────────
// Always routes through C# — it decides whether it's a file, image, or text
textarea.addEventListener("paste", e => {
    e.preventDefault();
    window.chrome.webview.postMessage({ type: "get-clipboard-files" });
});

function sendMessage() {
    _userScrolledUp = false;
    const text = textarea.value.trim();
    pushPromptHistory(text);
    historyIndex = -1;
    const activeAttachments = [...attachments.values()];

    if (text === "/model") {
        textarea.value = "";
        showModelPicker();
        return;
    }

    if (!text && activeAttachments.length === 0)
        return;

    addMessage("user", text || `(${activeAttachments.map(a => a.displayName).join(", ")})`);

    let fullMessage = text;
    const filePaths = [];

    for (const att of activeAttachments) {
        if (!att.includeFile) continue;
        if (att.content) {
            fullMessage += "\n\n" + att.content;
        } else if (att.filePath) {
            filePaths.push(att.filePath);
        }
    }

    if (filePaths.length > 0)
        fullMessage += "\n\nFiles attached:\n" + filePaths.map(p => `  - ${p}`).join("\n");

    // Close pending unconfirmed cards
    for (const [id, att] of attachments) {
        if (!att.includeFile) {
            const card = document.getElementById(`q-card-${id}`);
            if (card) {
                card.innerHTML = `<div class="question-text">📎 <strong>${escapeHtml(att.displayName)}</strong> — ✗ not sent</div>`;
                card.classList.add("question-answered");
            }
        }
    }

    attachments.clear();
    attachmentsEl.innerHTML = "";
    textarea.value = "";
    updateTokenEstimate();

    if (window.chrome?.webview) {
        setStreaming(true);
        addLoading();
        window.chrome.webview.postMessage({
            type: "chat",
            text: fullMessage,
            model: modelSelect.value,
            effort: effortSelect.value || null,
            permissionMode: permissionSelect.value,
            workingDirectory: localStorage.getItem("workingDirectory") || null,
            autoResume: localStorage.getItem("autoResume") === "true",
            autoSave: autoSaveValues[+autoSaveSlider.value]
        });
    }
}

function addMessage(role, text) {

    if (welcome) {
        welcome.remove();
    }

    const message = document.createElement("div");

    message.className = `message ${role}`;

    message.innerHTML = `<div class="bubble">${escapeHtml(text)}</div>`;

    messages.appendChild(message);
    decorateMessage(message);

    autoScroll();
}

window.chrome.webview.addEventListener("message", event => {

    if (event.data.type === "focus") {
        textarea.focus();
        textarea.select();
        return;
    }

    if (event.data.type === "version") {
        const el = document.querySelector(".about-meta");
        if (el) el.textContent = `v${event.data.text} · wluisdev`;
        return;
    }

    if (event.data.type === "theme") {
        document.documentElement.classList.toggle("light-theme", !event.data.isDark);
        return;
    }

    if (event.data.type === "toast") {
        showToast(event.data.text || "");
        return;
    }

    if (event.data.type === "history") {
        renderHistory(event.data.sessions);
        return;
    }

    if (event.data.type === "session-deleted") {
        const item = document.querySelector(`.history-item[data-session-id="${event.data.sessionId}"]`);
        if (item) item.remove();
        return;
    }

    if (event.data.type === "session-info") {
        currentSessionId = event.data.sessionId;
        return;
    }

    if (event.data.type === "branched") {
        renderBranchedMessages(event.data.sessionId, event.data.messages || []);
        hideResumeOverlay();
        return;
    }

    if (event.data.type === "diff") {
        renderDiff(event.data.stat, event.data.diff);
        return;
    }

    if (event.data.type === "attach-file") {
        addAttachment(event.data.filename, event.data.content, event.data.isBinary, event.data.filePath);
        return;
    }

    if (event.data.type === "insert-text") {
        insertAtCursor(event.data.text);
        return;
    }

    if (event.data.type === "chunk") {
        if (isUsageCapture) {
            usageBuffer += event.data.text;
            document.getElementById("usage-raw").textContent = usageBuffer;
        } else {
            appendChunk(event.data.text);
        }
        return;
    }

    if (event.data.type === "tokens") {
        appendTokens(event.data.text);
        return;
    }

    if (event.data.type === "cost-warning") {
        showCostWarning(event.data.text, event.data.blocked === true);
        return;
    }

    if (event.data.type === "cost-limits") {
        const s = event.data.sessionLimit;
        const d = event.data.dailyLimit;
        if (s != null) document.getElementById("cost-session-input").value = formatCost(s);
        if (d != null) document.getElementById("cost-daily-input").value = formatCost(d);
        document.getElementById("cost-block-toggle").checked = !!event.data.block;
        localStorage.setItem("costBlock", !!event.data.block);
        return;
    }

    if (event.data.type === "timing") {
        appendTiming(event.data.text);
        return;
    }

    if (event.data.type === "stream-done") {
        if (isUsageCapture) isUsageCapture = false;
        removeLoading();
        removeLiveTimer();
        if (currentStreamBubble) {
            finalizeBubbleStream(currentStreamBubble);
            currentStreamBubble = null;
        }
        setStreaming(false);
        return;
    }

    if (event.data.type === "tool_use" || event.data.type === "tool_result" || event.data.type === "tool_error") {
        appendToolEvent(event.data.type, event.data.name || "", event.data.input, event.data.text || "", event.data.id);
        return;
    }

    if (event.data.type === "tokens-live") {
        updateLiveTokens(event.data.text || "");
        return;
    }

    if (event.data.type === "permission_request") {
        openPermissionModal(event.data.tool || "", event.data.input || "", event.data.id || "", event.data.cwd || "");
        return;
    }

    if (event.data.type === "git-baseline-response") {
        const pending = pendingGitBaselineRequests.get(event.data.requestId);
        if (pending) {
            pendingGitBaselineRequests.delete(event.data.requestId);
            pending({ content: event.data.content || null, error: event.data.error || null });
        }
        return;
    }
});

let pendingPermissionToolId = null;
let pendingPermissionToolName = null;

function openPermissionModal(tool, input, id, cwd) {
    pendingPermissionToolId = id;
    pendingPermissionToolName = tool;
    document.getElementById("perm-modal-tool").textContent = tool;

    if (id) {
        const chip = messages.querySelector(`.tool-chip[data-tool-id="${CSS.escape(id)}"]`);
        if (chip) chip.classList.add("tool-pending");
    }

    const cwdRow = document.getElementById("perm-modal-cwd");
    const cwdPath = document.getElementById("perm-modal-cwd-path");
    if (cwd) {
        cwdPath.textContent = cwd;
        cwdRow.hidden = false;
    } else {
        cwdPath.textContent = "";
        cwdRow.hidden = true;
    }

    const pre = document.getElementById("perm-modal-input");
    let formatted = input;
    if (input) {
        try { formatted = JSON.stringify(JSON.parse(input), null, 2); }
        catch (_) { /* leave as-is */ }
    }
    pre.textContent = formatted || "(no input)";

    document.getElementById("perm-modal-overlay").classList.add("open");
}

function closePermissionModal() {
    document.getElementById("perm-modal-overlay").classList.remove("open");
    if (pendingPermissionToolId) {
        const chip = messages.querySelector(`.tool-chip[data-tool-id="${CSS.escape(pendingPermissionToolId)}"]`);
        if (chip) chip.classList.remove("tool-pending");
    }
    pendingPermissionToolId = null;
    pendingPermissionToolName = null;
}

function permissionAllow() {
    if (!pendingPermissionToolId) { closePermissionModal(); return; }
    window.chrome.webview.postMessage({
        type: "permission-response",
        toolUseId: pendingPermissionToolId,
        allow: true,
        reason: null
    });
    closePermissionModal();
}

function permissionAllowSession() {
    if (!pendingPermissionToolId) { closePermissionModal(); return; }
    window.chrome.webview.postMessage({
        type: "permission-response",
        toolUseId: pendingPermissionToolId,
        allow: true,
        reason: null,
        allowSession: pendingPermissionToolName
    });
    closePermissionModal();
}

function permissionDeny(reason) {
    if (!pendingPermissionToolId) { closePermissionModal(); return; }
    window.chrome.webview.postMessage({
        type: "permission-response",
        toolUseId: pendingPermissionToolId,
        allow: false,
        reason: reason || "denied by user"
    });
    closePermissionModal();
}

document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    if (!document.getElementById("perm-modal-overlay").classList.contains("open")) return;
    e.stopPropagation();
    permissionDeny("dismissed");
}, true);

const tokensToggle = document.getElementById("tokens-toggle");
tokensToggle.checked = localStorage.getItem("showTokens") !== "false";

function setShowTokens(checked) {
    localStorage.setItem("showTokens", checked);
}

// ── Token estimate ────────────────────────────────────────────
const tokenEstimateToggle = document.getElementById("token-estimate-toggle");
const tokenEstimateEl = document.getElementById("token-estimate");
tokenEstimateToggle.checked = localStorage.getItem("showTokenEstimate") === "true";

function setShowTokenEstimate(checked) {
    localStorage.setItem("showTokenEstimate", checked);
    updateTokenEstimate();
}

function estimateTokens(text) {
    if (!text) return 0;
    // Heuristic: if predominantly code (high symbol/whitespace ratio), use /3.5;
    // otherwise natural text, use /5
    const codeLike = (text.match(/[{}\[\]<>()=;:\/\\|@#$%^&*+\-_`"']/g) || []).length;
    const ratio = codeLike / text.length;
    const divisor = ratio > 0.08 ? 3.5 : 5;
    return Math.ceil(text.length / divisor);
}

function updateTokenEstimate() {
    if (!tokenEstimateToggle.checked) {
        tokenEstimateEl.textContent = "";
        return;
    }
    const tokens = estimateTokens(textarea.value);
    if (tokens === 0) { tokenEstimateEl.textContent = ""; return; }
    tokenEstimateEl.textContent = tokens >= 1000
        ? `~${(tokens / 1000).toFixed(1)}k tok`
        : `~${tokens} tok`;
}

const sendEnterToggle = document.getElementById("send-enter-toggle");
sendEnterToggle.checked = localStorage.getItem("sendWithEnter") !== "false";

function setSendWithEnter(checked) {
    localStorage.setItem("sendWithEnter", checked);
}

// Auto-resume
const autoResumeToggle = document.getElementById("auto-resume-toggle");
autoResumeToggle.checked = localStorage.getItem("autoResume") === "true";

function setAutoResume(checked) {
    localStorage.setItem("autoResume", checked);
}

// Working directory
const workingDirInput = document.getElementById("working-dir-input");
workingDirInput.value = localStorage.getItem("workingDirectory") || "";

function setWorkingDirectory(value) {
    localStorage.setItem("workingDirectory", value.trim());
}

function clearWorkingDirectory() {
    workingDirInput.value = "";
    localStorage.setItem("workingDirectory", "");
    workingDirInput.focus();
}

// ── Cost limits ────────────────────────────────────────────────
function parseCost(value) {
    const n = parseFloat(String(value).replace(",", "."));
    return isFinite(n) && n > 0 ? n : null;
}

function formatCost(value) {
    const n = typeof value === "number" ? value : parseCost(value);
    if (n == null) return "";
    return n.toFixed(2).replace(".", ",");
}

function formatCostLimitInput(el) {
    el.value = formatCost(el.value);
}

let _costLimitSaveTimer = null;
function saveCostLimits() {
    clearTimeout(_costLimitSaveTimer);
    _costLimitSaveTimer = setTimeout(() => {
        const sl = parseCost(localStorage.getItem("costSessionLimit"));
        const dl = parseCost(localStorage.getItem("costDailyLimit"));
        const block = localStorage.getItem("costBlock") === "true";
        window.chrome.webview.postMessage({
            type: "set-cost-limits",
            sessionLimit: sl,
            dailyLimit: dl,
            block
        });
    }, 400);
}

function setCostLimit(which, value) {
    const v = parseCost(value);
    if (which === "session") localStorage.setItem("costSessionLimit", v ?? "");
    else localStorage.setItem("costDailyLimit", v ?? "");
    dismissCostWarning();
    saveCostLimits();
}

function setCostBlock(checked) {
    localStorage.setItem("costBlock", !!checked);
    dismissCostWarning();
    saveCostLimits();
}

function clearCostLimit(which) {
    const id = which === "session" ? "cost-session-input" : "cost-daily-input";
    const inp = document.getElementById(id);
    inp.value = "";
    setCostLimit(which, "");
    inp.focus();
}

(function loadCostLimitsFromStorage() {
    const s = localStorage.getItem("costSessionLimit");
    const d = localStorage.getItem("costDailyLimit");
    const block = localStorage.getItem("costBlock") === "true";
    if (s) document.getElementById("cost-session-input").value = formatCost(s);
    if (d) document.getElementById("cost-daily-input").value = formatCost(d);
    document.getElementById("cost-block-toggle").checked = block;
    // Sync with disk to handle edits from other VS instances
    try { window.chrome.webview.postMessage({ type: "get-cost-limits" }); } catch (_) { }
})();

function showCostWarning(text, blocked) {
    const el = document.getElementById("cost-warning");
    const prefix = blocked ? "Message blocked — " : "Cost limit reached — ";
    document.getElementById("cost-warning-text").textContent = prefix + text;
    el.classList.toggle("blocked", !!blocked);
    el.style.display = "flex";

    if (blocked) {
        const userMsgs = messages.querySelectorAll(".message.user");
        const last = userMsgs[userMsgs.length - 1];
        if (last && !last.classList.contains("blocked")) {
            last.classList.add("blocked");
            const badge = document.createElement("div");
            badge.className = "blocked-badge";
            badge.textContent = "🚫 blocked by cost limit";
            last.appendChild(badge);
        }
    }
}

function dismissCostWarning() {
    document.getElementById("cost-warning").style.display = "none";
}

// Ctrl+Scroll to resize textarea font
let composerFontSize = parseFloat(localStorage.getItem("composerFontSize") || "13");
textarea.style.fontSize = composerFontSize + "px";

textarea.addEventListener("wheel", e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    composerFontSize = Math.max(10, Math.min(24, composerFontSize + (e.deltaY < 0 ? 1 : -1)));
    textarea.style.fontSize = composerFontSize + "px";
    localStorage.setItem("composerFontSize", composerFontSize);
}, { passive: false });

let timeUnit = localStorage.getItem("timeUnit") || "s";
(function () {
    document.getElementById("unit-" + timeUnit).classList.add("active");
})();

function setTimeUnit(unit) {
    timeUnit = unit;
    localStorage.setItem("timeUnit", unit);
    document.querySelectorAll(".unit-btn").forEach(b => b.classList.remove("active"));
    document.getElementById("unit-" + unit).classList.add("active");
}

function formatMs(ms) {
    if (timeUnit === "ms") return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    const m = Math.floor(ms / 60000);
    const s = ((ms % 60000) / 1000).toFixed(0);
    return `${m}m ${s}s`;
}

function appendTokens(text) {
    const parts = text.split("/").map(Number);
    const inp = parts[0] || 0;
    const out = parts[1] || 0;
    const cacheRead = parts[2] || 0;
    sessionIn += inp;
    sessionOut += out;
    updateUsageSessionValues();
    if (!tokensToggle.checked) return;
    const fmt = n => n >= 1000 ? `${(n / 1000).toFixed(1)}k` : `${n}`;
    const el = document.createElement("div");
    el.className = "timing";
    const cachePart = cacheRead > 0 ? ` · ↻ ${fmt(cacheRead)} cached` : "";
    el.textContent = `tokens: ↑ ${fmt(inp)} in${cachePart} · ↓ ${fmt(out)} out`;
    messages.appendChild(el);
    autoScroll();
}

function appendTiming(text) {
    const mode = timingSelect.value;
    if (mode === "none") return;
    if (mode === "simple" && !text.startsWith("total:")) return;
    const formatted = text.replace(/(\d+)ms/g, (_, n) => formatMs(Number(n)));
    const el = document.createElement("div");
    el.className = "timing";
    el.textContent = `⏱ ${formatted}`;
    messages.appendChild(el);
    autoScroll();
}

function applyMarkdown(bubble, raw) {
    bubble.innerHTML = renderMarkdown(raw);
    bubble.querySelectorAll("pre code").forEach(el => {
        if (typeof hljs !== "undefined") hljs.highlightElement(el);
    });
    bubble.querySelectorAll("pre").forEach(pre => {
        if (pre.querySelector(".apply-btn")) return;
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "apply-btn";
        btn.textContent = "Apply";
        btn.title = "Insert this code at the cursor in the active editor";
        pre.appendChild(btn);
    });
}

function escapeHtmlRaw(text) {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function renderMarkdown(raw) {
    // Protect fenced code blocks
    const blocks = [];
    let text = raw.replace(/```(\w*)\n?([\s\S]*?)```/g, (_, lang, code) => {
        const i = blocks.length;
        const cls = lang ? ` class="language-${lang}"` : '';
        blocks.push(`<pre><code${cls}>${escapeHtmlRaw(code.trimEnd())}</code></pre>`);
        return `\x00B${i}\x00`;
    });

    // Protect inline code
    const inlines = [];
    text = text.replace(/`([^`\n]+)`/g, (_, code) => {
        const i = inlines.length;
        inlines.push(`<code>${escapeHtml(code)}</code>`);
        return `\x00I${i}\x00`;
    });

    // Headers
    text = text.replace(/^#{3} (.+)$/gm, '<h3>$1</h3>');
    text = text.replace(/^#{2} (.+)$/gm, '<h2>$1</h2>');
    text = text.replace(/^# (.+)$/gm, '<h1>$1</h1>');

    // Bold / italic
    text = text.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    text = text.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/\*([^\s*][^*\n]*?)\*/g, '<em>$1</em>');

    // Unordered lists
    text = text.replace(/((?:^[ \t]*[-*+] .+\n?)+)/gm, m => {
        const items = m.trim().split('\n').map(l => `<li>${l.replace(/^[ \t]*[-*+] /, '')}</li>`).join('');
        return `<ul>${items}</ul>`;
    });

    // Ordered lists
    text = text.replace(/((?:^\d+\. .+\n?)+)/gm, m => {
        const items = m.trim().split('\n').map(l => `<li>${l.replace(/^\d+\. /, '')}</li>`).join('');
        return `<ol>${items}</ol>`;
    });

    // Tables
    text = text.replace(/((?:^[ \t]*\|.+\|[ \t]*\n?){2,})/gm, m => {
        const lines = m.split('\n').filter(l => l.trim().startsWith('|'));
        if (lines.length < 2) return m;
        const sepInner = lines[1].trim().replace(/^\||\|$/g, '');
        if (!/^[\s\-:|]+$/.test(sepInner) || !/-/.test(sepInner)) return m;

        const splitCells = line => line.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
        const header = splitCells(lines[0]);
        const aligns = splitCells(lines[1]).map(c => {
            const t = c.trim();
            if (t.startsWith(':') && t.endsWith(':')) return 'center';
            if (t.endsWith(':')) return 'right';
            return 'left';
        });
        const rows = lines.slice(2).map(splitCells);

        const cellAlign = i => aligns[i] || 'left';
        const thead = '<thead><tr>' + header.map((h, i) =>
            `<th style="text-align:${cellAlign(i)}">${h}</th>`).join('') + '</tr></thead>';
        const tbody = '<tbody>' + rows.map(r =>
            '<tr>' + r.map((c, i) =>
                `<td style="text-align:${cellAlign(i)}">${c}</td>`).join('') + '</tr>'
        ).join('') + '</tbody>';

        return `<table class="md-table">${thead}${tbody}</table>\n`;
    });

    // Links
    text = text.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank">$1</a>');

    // Paragraphs
    text = text.split(/\n{2,}/).map(block => {
        block = block.trim();
        if (!block) return '';
        if (/^(<(h[1-3]|ul|ol|pre|table)|\x00B)/.test(block)) return block;
        return `<p>${block.replace(/\n/g, '<br>')}</p>`;
    }).join('');

    // Restore
    text = text.replace(/\x00B(\d+)\x00/g, (_, i) => blocks[+i]);
    text = text.replace(/\x00I(\d+)\x00/g, (_, i) => inlines[+i]);

    return text;
}

function ensureStreamBubble() {
    if (currentStreamBubble) return currentStreamBubble;
    const msg = document.createElement("div");
    msg.className = "message assistant";
    msg.innerHTML = `<div class="bubble"></div>`;
    messages.appendChild(msg);
    decorateMessage(msg);
    currentStreamBubble = msg.querySelector(".bubble");
    return currentStreamBubble;
}

function getActiveTextSeg(bubble) {
    const last = bubble.lastElementChild;
    if (last && last.classList.contains("bubble-text") && last.dataset.finalized !== "1")
        return last;
    const seg = document.createElement("div");
    seg.className = "bubble-text";
    seg.dataset.raw = "";
    bubble.appendChild(seg);
    return seg;
}

function finalizeActiveSeg(bubble) {
    const last = bubble.lastElementChild;
    if (!last || !last.classList.contains("bubble-text") || last.dataset.finalized === "1") return;
    const raw = last.dataset.raw || "";
    if (raw) applyMarkdown(last, raw);
    last.dataset.finalized = "1";
}

function finalizeBubbleStream(bubble) {
    // Finalize any unfinished text segment AND legacy bubbles that used dataset.raw on the bubble itself
    if (bubble.dataset.raw && !bubble.querySelector(".bubble-text")) {
        applyMarkdown(bubble, bubble.dataset.raw);
        bubble.dataset.finalized = "1";
        return;
    }
    finalizeActiveSeg(bubble);
}

function appendChunk(text) {
    removeLoading();
    const bubble = ensureStreamBubble();
    const seg = getActiveTextSeg(bubble);
    seg.dataset.raw = (seg.dataset.raw || "") + text;
    seg.textContent = seg.dataset.raw;
    if (isStreaming) {
        ensureLiveTimer();
        bumpEstimatedOut(text.length);
    }
    autoScroll();
}

function summarizeToolInput(name, inputJson) {
    if (!inputJson) return "";
    let input;
    try { input = JSON.parse(inputJson); } catch { return ""; }
    if (!input || typeof input !== "object") return "";
    const arg = input.file_path || input.path || input.command || input.pattern || input.url || input.notebook_path || input.query;
    if (arg) {
        const s = String(arg);
        return s.length > 80 ? "…" + s.slice(-79) : s;
    }
    const keys = Object.keys(input);
    return keys.length ? keys[0] : "";
}

// ---- diff viewer state ----
const DIFF_TOOLS = new Set(["Edit", "Write", "MultiEdit"]);
const toolInputData = new Map();      // toolId → parsed input object
const gitBaselineCache = new Map();   // filePath → { content|null, error|null }
const pendingGitBaselineRequests = new Map(); // requestId → resolve()
const PREVIEW_MAX_LINES = 30;

function requestGitBaseline(filePath) {
    if (!filePath) return Promise.resolve({ content: null, error: "no path" });
    const cached = gitBaselineCache.get(filePath);
    if (cached) return Promise.resolve(cached);
    return new Promise(resolve => {
        const requestId = "gb-" + Math.random().toString(36).slice(2);
        pendingGitBaselineRequests.set(requestId, result => {
            gitBaselineCache.set(filePath, result);
            resolve(result);
        });
        window.chrome.webview.postMessage({
            type: "request-git-baseline",
            requestId,
            path: filePath
        });
    });
}

function computeLineDiff(oldText, newText) {
    const oldLines = (oldText || "").split("\n");
    const newLines = (newText || "").split("\n");
    const m = oldLines.length, n = newLines.length;

    // Guard against huge inputs (DP table too big)
    if (m * n > 2_000_000) {
        const out = [];
        for (let i = 0; i < m; i++) out.push({ type: "-", line: oldLines[i], oldNum: i + 1, newNum: null });
        for (let j = 0; j < n; j++) out.push({ type: "+", line: newLines[j], oldNum: null, newNum: j + 1 });
        return out;
    }

    const dp = Array.from({ length: m + 1 }, () => new Int32Array(n + 1));
    for (let i = m - 1; i >= 0; i--) {
        for (let j = n - 1; j >= 0; j--) {
            dp[i][j] = oldLines[i] === newLines[j]
                ? dp[i + 1][j + 1] + 1
                : Math.max(dp[i + 1][j], dp[i][j + 1]);
        }
    }

    const out = [];
    let i = 0, j = 0;
    while (i < m && j < n) {
        if (oldLines[i] === newLines[j]) {
            out.push({ type: " ", line: oldLines[i], oldNum: i + 1, newNum: j + 1 });
            i++; j++;
        } else if (dp[i + 1][j] >= dp[i][j + 1]) {
            out.push({ type: "-", line: oldLines[i], oldNum: i + 1, newNum: null });
            i++;
        } else {
            out.push({ type: "+", line: newLines[j], oldNum: null, newNum: j + 1 });
            j++;
        }
    }
    while (i < m) { out.push({ type: "-", line: oldLines[i], oldNum: i + 1, newNum: null }); i++; }
    while (j < n) { out.push({ type: "+", line: newLines[j], oldNum: null, newNum: j + 1 }); j++; }
    return out;
}

function trimDiffToChangedHunks(lines, contextSize) {
    // Keep changed lines and `contextSize` of surrounding context. Insert gap markers.
    const ctx = contextSize ?? 3;
    const keep = new Array(lines.length).fill(false);
    for (let i = 0; i < lines.length; i++) {
        if (lines[i].type !== " ") {
            for (let k = Math.max(0, i - ctx); k <= Math.min(lines.length - 1, i + ctx); k++) keep[k] = true;
        }
    }
    const out = [];
    let lastKept = -2;
    for (let i = 0; i < lines.length; i++) {
        if (!keep[i]) continue;
        if (lastKept >= 0 && i > lastKept + 1) out.push({ type: "gap" });
        out.push(lines[i]);
        lastKept = i;
    }
    return out;
}

function renderDiffHtml(diffLines) {
    const html = [];
    for (const ln of diffLines) {
        if (ln.type === "gap") {
            html.push('<div class="diff-line diff-gap">⋯</div>');
            continue;
        }
        const cls = ln.type === "+" ? "diff-add" : ln.type === "-" ? "diff-del" : "diff-ctx";
        const oldNum = ln.oldNum != null ? String(ln.oldNum) : "";
        const newNum = ln.newNum != null ? String(ln.newNum) : "";
        const indicator = ln.type;
        const safe = escapeHtml(ln.line ?? "");
        html.push(`<div class="diff-line ${cls}"><span class="diff-lineno">${oldNum}</span><span class="diff-lineno">${newNum}</span><span class="diff-indicator">${indicator}</span><span class="diff-text">${safe}</span></div>`);
    }
    return html.join("");
}

function escapeHtml(s) {
    return s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function buildDiffForTool(toolName, input) {
    // Returns { filePath, diffLines, hasBaseline, error }
    if (toolName === "Edit") {
        const lines = computeLineDiff(input.old_string || "", input.new_string || "");
        return { filePath: input.file_path || "", diffLines: lines };
    }
    if (toolName === "MultiEdit") {
        const edits = Array.isArray(input.edits) ? input.edits : [];
        const all = [];
        edits.forEach((e, idx) => {
            if (idx > 0) all.push({ type: "gap" });
            const lines = computeLineDiff(e.old_string || "", e.new_string || "");
            all.push(...lines);
        });
        return { filePath: input.file_path || "", diffLines: all };
    }
    if (toolName === "Write") {
        const filePath = input.file_path || "";
        const newContent = input.content || "";
        const { content: baseline, error } = await requestGitBaseline(filePath);
        if (baseline == null) {
            // New file (or git unavailable) — show all as +
            const lines = newContent.split("\n").map((line, idx) => ({ type: "+", line, oldNum: null, newNum: idx + 1 }));
            return { filePath, diffLines: lines, hasBaseline: false, error };
        }
        const lines = computeLineDiff(baseline, newContent);
        return { filePath, diffLines: lines, hasBaseline: true };
    }
    return { filePath: "", diffLines: [], error: "unsupported tool" };
}

async function toggleToolDiff(chip) {
    const existing = chip.querySelector(".tool-diff-inline");
    if (existing) {
        existing.remove();
        chip.classList.remove("tool-chip-expanded");
        return;
    }
    const id = chip.dataset.toolId;
    const data = id ? toolInputData.get(id) : null;
    if (!data) return;

    const container = document.createElement("div");
    container.className = "tool-diff-inline";
    container.innerHTML = '<div class="tool-diff-loading">computing diff…</div>';
    chip.appendChild(container);
    chip.classList.add("tool-chip-expanded");

    try {
        const { filePath, diffLines, hasBaseline, error } = await buildDiffForTool(data.name, data.input);
        if (error && (!diffLines || diffLines.length === 0)) {
            container.innerHTML = `<div class="tool-diff-error">${escapeHtml(error)}</div>`;
            return;
        }
        const trimmed = trimDiffToChangedHunks(diffLines, 3);
        const preview = trimmed.slice(0, PREVIEW_MAX_LINES);
        const truncated = trimmed.length > PREVIEW_MAX_LINES;
        const noteHtml = (data.name === "Write" && hasBaseline === false)
            ? '<div class="tool-diff-note">new file (no git baseline)</div>'
            : "";
        container.innerHTML =
            noteHtml +
            `<div class="tool-diff-body">${renderDiffHtml(preview)}</div>` +
            `<div class="tool-diff-actions">
                ${truncated ? `<span class="tool-diff-truncated">+${trimmed.length - PREVIEW_MAX_LINES} mais</span>` : ""}
                <button class="tool-diff-full-btn">ver completo</button>
            </div>`;
        const btn = container.querySelector(".tool-diff-full-btn");
        btn.addEventListener("click", e => {
            e.stopPropagation();
            openDiffModal(filePath, diffLines, data.name, hasBaseline);
        });
    } catch (ex) {
        container.innerHTML = `<div class="tool-diff-error">${escapeHtml(String(ex))}</div>`;
    }
    autoScroll();
}

function openDiffModal(filePath, diffLines, toolName, hasBaseline) {
    const overlay = document.getElementById("diff-modal-overlay");
    document.getElementById("diff-modal-path").textContent = filePath || "(no path)";
    const subtitle = document.getElementById("diff-modal-subtitle");
    const added = diffLines.filter(l => l.type === "+").length;
    const removed = diffLines.filter(l => l.type === "-").length;
    let sub = `${toolName} • +${added} -${removed}`;
    if (toolName === "Write" && hasBaseline === false) sub += " • new file";
    subtitle.textContent = sub;
    document.getElementById("diff-modal-body").innerHTML = renderDiffHtml(diffLines);
    overlay.classList.add("open");
}

function closeDiffModal() {
    document.getElementById("diff-modal-overlay").classList.remove("open");
}

document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    const overlay = document.getElementById("diff-modal-overlay");
    if (!overlay || !overlay.classList.contains("open")) return;
    e.stopPropagation();
    closeDiffModal();
}, true);

function appendToolEvent(kind, name, inputJson, text, id) {
    removeLoading();
    const bubble = ensureStreamBubble();

    if (kind === "tool_use") {
        // Dedupe by id — JSONL watcher may emit the same tool_use after stream-json already did
        if (id) {
            const existing = bubble.querySelector(`.tool-chip[data-tool-id="${CSS.escape(id)}"]`);
            if (existing) {
                // Refresh the arg in case the watcher has richer input
                const arg = summarizeToolInput(name, inputJson);
                let argEl = existing.querySelector(".tool-arg");
                if (arg) {
                    if (!argEl) {
                        argEl = document.createElement("span");
                        argEl.className = "tool-arg";
                        // Insert before caret if present so caret stays at the end
                        const caret = existing.querySelector(".tool-diff-caret");
                        if (caret) existing.insertBefore(argEl, caret);
                        else existing.appendChild(argEl);
                    }
                    argEl.textContent = `(${arg})`;
                }
                // Refresh stored input — JSONL watcher payload is authoritative (full content)
                if (DIFF_TOOLS.has(name) && inputJson) {
                    try {
                        const parsed = JSON.parse(inputJson);
                        if (parsed && typeof parsed === "object") {
                            toolInputData.set(id, { name, input: parsed });
                            if (!existing.classList.contains("tool-chip-diffable")) {
                                existing.classList.add("tool-chip-diffable");
                                const caret = document.createElement("span");
                                caret.className = "tool-diff-caret";
                                caret.textContent = "▸";
                                existing.appendChild(caret);
                                existing.addEventListener("click", e => {
                                    if (e.target.closest(".tool-diff-inline")) return;
                                    toggleToolDiff(existing);
                                });
                            }
                        }
                    } catch { /* ignore */ }
                }
                if (isStreaming) ensureLiveTimer();
                autoScroll();
                return;
            }
        }

        finalizeActiveSeg(bubble);
        const chip = document.createElement("div");
        chip.className = "tool-chip";
        if (id) {
            chip.dataset.toolId = id;
            if (id === pendingPermissionToolId) chip.classList.add("tool-pending");
        }

        // For diff-supported tools, parse and store input + mark chip clickable
        let diffable = false;
        if (id && DIFF_TOOLS.has(name) && inputJson) {
            try {
                const parsed = JSON.parse(inputJson);
                if (parsed && typeof parsed === "object") {
                    toolInputData.set(id, { name, input: parsed });
                    diffable = true;
                    chip.classList.add("tool-chip-diffable");
                }
            } catch { /* leave unclickable */ }
        }

        const dot = document.createElement("span");
        dot.className = "tool-dot";
        dot.textContent = "●";

        const label = document.createElement("span");
        label.className = "tool-name";
        label.textContent = name || "tool";

        chip.appendChild(dot);
        chip.appendChild(label);

        const arg = summarizeToolInput(name, inputJson);
        if (arg) {
            const argEl = document.createElement("span");
            argEl.className = "tool-arg";
            argEl.textContent = `(${arg})`;
            chip.appendChild(argEl);
        }

        if (diffable) {
            const caret = document.createElement("span");
            caret.className = "tool-diff-caret";
            caret.textContent = "▸";
            chip.appendChild(caret);
            chip.addEventListener("click", e => {
                // Ignore clicks on the inline diff body (so user can select text)
                if (e.target.closest(".tool-diff-inline")) return;
                toggleToolDiff(chip);
            });
        }

        bubble.appendChild(chip);
    } else if (kind === "tool_result" || kind === "tool_error") {
        let chip = null;
        if (id) chip = bubble.querySelector(`.tool-chip[data-tool-id="${CSS.escape(id)}"]`);
        if (!chip) {
            // fall back to last chip if no id match
            const chips = bubble.querySelectorAll(".tool-chip");
            chip = chips[chips.length - 1] || null;
        }
        if (!chip) return;
        if (kind === "tool_error") chip.classList.add("tool-error");

        if (text && text.trim()) {
            const firstLine = text.split(/\r?\n/)[0];
            const summaryText = "↳ " + (firstLine.length > 160 ? firstLine.slice(0, 160) + "…" : firstLine);
            let summary = chip.querySelector(".tool-summary");
            if (!summary) {
                summary = document.createElement("div");
                summary.className = "tool-summary";
                chip.appendChild(summary);
            }
            summary.textContent = summaryText;
        }
    }
    if (isStreaming) ensureLiveTimer();
    autoScroll();
}

let _loadingTimer = null;
let _loadingStart = 0;

function addLoading() {
    const el = document.createElement("div");
    el.className = "message assistant loading";
    el.innerHTML = `<div class="bubble"><span class="dots"><span>.</span><span>.</span><span>.</span></span><span class="live-timer">0.0s</span></div>`;
    messages.appendChild(el);
    autoScroll();
    _loadingStart = Date.now();
    _loadingTimer = setInterval(() => {
        const timerEl = messages.querySelector(".live-timer");
        if (!timerEl) return;
        const ms = Date.now() - _loadingStart;
        timerEl.textContent = ms < 60000
            ? `${(ms / 1000).toFixed(1)}s`
            : `${Math.floor(ms / 60000)}m ${((ms % 60000) / 1000).toFixed(0)}s`;
    }, 100);
}

function removeLoading() {
    clearInterval(_loadingTimer);
    _loadingTimer = null;
    const el = messages.querySelector(".loading");
    if (el) el.remove();
    if (isStreaming) ensureLiveTimer();
}

let _liveTimerStart = 0;
let _liveTimerInterval = null;
let _liveTokens = { in: 0, out: 0, cached: 0 };
let _estimatedOutChars = 0;
let _realOutTokens = 0;

function renderLiveTimer() {
    const el = messages.querySelector(".stream-timer");
    if (!el) return;
    const ms = Date.now() - _liveTimerStart;
    const fmt = n => n >= 1000 ? `${(n / 1000).toFixed(1)}k` : `${n}`;
    const time = ms < 60000
        ? `${(ms / 1000).toFixed(1)}s`
        : `${Math.floor(ms / 60000)}m ${((ms % 60000) / 1000).toFixed(0)}s`;
    let txt = `⏱ ${time}`;

    const estOut = Math.floor(_estimatedOutChars / 3.7);
    const outShown = Math.max(_realOutTokens, estOut);
    const isEstimate = estOut > _realOutTokens;

    const parts = [];
    if (_liveTokens.in) parts.push(`↑ ${fmt(_liveTokens.in)}`);
    if (_liveTokens.cached) parts.push(`↻ ${fmt(_liveTokens.cached)}`);
    if (outShown) parts.push(`↓ ${isEstimate ? "~" : ""}${fmt(outShown)}`);
    if (parts.length) txt += " · " + parts.join(" · ");

    el.textContent = txt;
}

function ensureLiveTimer() {
    let el = messages.querySelector(".stream-timer");
    if (!el) {
        // Inherit elapsed from loading bubble if it was up; do NOT kill the bubble.
        // The loading dots stay as visual cue until real content arrives.
        const inheritedStart = _loadingStart || Date.now();
        _liveTimerStart = inheritedStart;
        el = document.createElement("div");
        el.className = "stream-timer";
        messages.appendChild(el);
        renderLiveTimer();

        // Hide the inner live-timer of the loading bubble to avoid duplicate time display
        const innerTimer = messages.querySelector(".loading .live-timer");
        if (innerTimer) innerTimer.style.display = "none";
    }
    if (!_liveTimerInterval) {
        _liveTimerInterval = setInterval(renderLiveTimer, 100);
    }
    messages.appendChild(el);
    autoScroll();
}

function updateLiveTokens(text) {
    const parts = (text || "").split("/").map(Number);
    const newIn = parts[0] || 0;
    const newOut = parts[1] || 0;
    const newCached = parts[2] || 0;
    // Don't overwrite in/cached with zeros — message_delta events carry partial
    // usage (output only); message_start carries the real input numbers.
    if (newIn > 0) _liveTokens.in = newIn;
    if (newCached > 0) _liveTokens.cached = newCached;
    // Only recalibrate output when it grows. Duplicate events with the same
    // cumulative usage would otherwise wipe out chars accumulated by
    // bumpEstimatedOut between events.
    if (newOut > _realOutTokens) {
        _realOutTokens = newOut;
        _estimatedOutChars = Math.max(_estimatedOutChars, _realOutTokens * 3.7);
    }
    _liveTokens.out = _realOutTokens;
    if (isStreaming) ensureLiveTimer();
    renderLiveTimer();
}

function bumpEstimatedOut(chars) {
    _estimatedOutChars += chars;
    renderLiveTimer();
}

function removeLiveTimer() {
    if (_liveTimerInterval) { clearInterval(_liveTimerInterval); _liveTimerInterval = null; }
    const el = messages.querySelector(".stream-timer");
    if (el) el.remove();
    _liveTokens = { in: 0, out: 0, cached: 0 };
    _estimatedOutChars = 0;
    _realOutTokens = 0;
}

function clearChat() {
    messages.innerHTML = `
        <div class="welcome">
            <div class="hero"><span class="logo">✺</span> Claude Code Studio</div>
            <div class="bot">🤖</div>
        </div>`;

    welcome = messages.querySelector(".welcome");
    textarea.value = "";
    sessionIn = 0;
    sessionOut = 0;
    msgCounter = 0;
    currentSessionId = null;
    updateUsageSessionValues();

    window.chrome.webview.postMessage({ type: "clear" });
}

function renderBranchedMessages(newSessionId, msgs) {
    if (welcome) { welcome.remove(); welcome = null; }
    messages.innerHTML = "";
    msgCounter = 0;
    currentSessionId = newSessionId;

    for (const m of msgs) {
        if (m.role === "user") {
            addMessage("user", m.text || "");
        } else if (m.role === "assistant") {
            const msg = document.createElement("div");
            msg.className = "message assistant";
            const bubble = document.createElement("div");
            bubble.className = "bubble";
            bubble.dataset.raw = m.text || "";
            messages.appendChild(msg);
            msg.appendChild(bubble);
            applyMarkdown(bubble, m.text || "");
            decorateMessage(msg);
        }
    }
    autoScroll();
}

function addAttachment(filename, content, isBinary, filePath) {
    const imageExts = ["png", "jpg", "jpeg", "gif", "bmp", "ico", "webp", "tiff"];
    const ext = filename.split(".").pop().toLowerCase();
    const isImage = imageExts.includes(ext);

    const displayName = isImage ? `image${++imageCounter}.${ext}` : filename;

    const id = attachmentIdCounter++;
    attachments.set(id, { displayName, content: isBinary ? null : content, filePath: filePath || null, includeFile: false });

    const chip = document.createElement("div");
    chip.className = "attachment-chip attachment-binary";
    chip.dataset.attId = id;
    const nameClass = filePath ? "attachment-name clickable" : "attachment-name";
    chip.innerHTML = `<span class="${nameClass}" title="${filePath ? "Click to open" : ""}">${displayName}</span><button class="attachment-remove" title="Remove">×</button>`;
    if (filePath) {
        chip.querySelector(".attachment-name").addEventListener("click", () => {
            window.chrome.webview.postMessage({ type: "open-file", path: filePath });
        });
    }
    chip.querySelector(".attachment-remove").addEventListener("click", () => {
        attachments.delete(id);
        chip.remove();
        document.getElementById(`q-card-${id}`)?.remove();
    });
    attachmentsEl.appendChild(chip);

    showFileQuestion(id, displayName);
}

function showFileQuestion(id, displayName) {
    if (welcome) {
        welcome.remove();
        welcome = null;
    }

    const card = document.createElement("div");
    card.className = "question-card";
    card.id = `q-card-${id}`;
    card.innerHTML = `
<div class="question-text">📎 <strong>${escapeHtml(displayName)}</strong> — Do you want Claude to read this file?</div>
<div class="question-buttons">
<button class="q-btn q-yes" onclick="confirmFile(${id}, true)">Yes</button>
<button class="q-btn q-no" onclick="confirmFile(${id}, false)">No</button>
</div>`;
    messages.appendChild(card);
    autoScroll();
}

function confirmFile(id, include) {
    const att = attachments.get(id);
    if (att) att.includeFile = include;

    const card = document.getElementById(`q-card-${id}`);
    if (card) {
        card.innerHTML = `<div class="question-text">📎 <strong>${escapeHtml(att?.displayName || "")}</strong> — ${include ? "✓ will be sent to Claude" : "✗ ignored"}</div>`;
        card.classList.add("question-answered");
    }

    const chip = document.querySelector(`[data-att-id="${id}"]`);
    if (!include) {
        attachments.delete(id);
        chip?.remove();
    } else {
        chip?.classList.remove("attachment-binary");
    }
}

function insertAtCursor(text) {
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    textarea.value = textarea.value.substring(0, start) + text + textarea.value.substring(end);
    textarea.selectionStart = textarea.selectionEnd = start + text.length;
    textarea.focus();
    updateTokenEstimate();
}

function renderHistory(sessions) {
    const list = document.getElementById("history-list");
    if (!sessions || sessions.length === 0) {
        list.innerHTML = '<div class="cmd-item" style="color:#555;font-family:inherit">No sessions found</div>';
        return;
    }
    list.innerHTML = sessions.map(s => {
        const tok = s.tokens > 1000 ? `${(s.tokens / 1000).toFixed(1)}k tok` : `${s.tokens} tok`;
        return `<div class="cmd-item history-item" data-session-id="${escapeAttr(s.id)}">
  <div class="history-header">
    <div class="history-preview" onclick="resumeSession('${escapeAttr(s.id)}')">${escapeHtml(s.preview)}</div>
    <button class="history-delete" onclick="deleteSession('${escapeAttr(s.id)}')" title="Delete session">×</button>
  </div>
  <div class="history-date">${escapeHtml(s.date)} · ${tok}</div>
</div>`;
    }).join("");
}

function resumeSession(sessionId) {
    document.getElementById("history-menu").classList.remove("open");
    showResumeOverlay();
    window.chrome.webview.postMessage({ type: "resume-session", sessionId });
}

function showResumeOverlay() {
    let ov = document.getElementById("resume-overlay");
    if (!ov) {
        ov = document.createElement("div");
        ov.id = "resume-overlay";
        ov.className = "resume-overlay";
        ov.innerHTML = `<div class="resume-overlay-inner"><span class="dots"><span>.</span><span>.</span><span>.</span></span><span>Loading session…</span></div>`;
        document.body.appendChild(ov);
    }
    ov.classList.add("visible");
}

function hideResumeOverlay() {
    const ov = document.getElementById("resume-overlay");
    if (ov) ov.classList.remove("visible");
}

function escapeHtml(text) {

    const div = document.createElement("div");

    div.innerText = text;

    return div.innerHTML;
}

function deleteSession(sessionId) {
    if (!confirm("Delete this session? This cannot be undone.")) return;
    window.chrome.webview.postMessage({ type: "delete-session", sessionId });
}

// ── Open usage window ─────────────────────────────────────────
function openUsageWindow() {
    window.chrome.webview.postMessage({ type: "open-usage" });
}

// ── Open MCP servers window ───────────────────────────────────
function openMcpWindow() {
    window.chrome.webview.postMessage({ type: "open-mcp" });
}

// ── Find in chat (Ctrl+F) ─────────────────────────────────────
let _findMatches = [];
let _findIndex = -1;

document.addEventListener("keydown", e => {
    if (e.ctrlKey && (e.key === "f" || e.key === "F")) {
        e.preventDefault();
        openFindBar();
        return;
    }
    if (e.key === "Escape" && document.getElementById("find-bar").classList.contains("open")) {
        e.preventDefault();
        closeFindBar();
    }
});

function openFindBar() {
    const bar = document.getElementById("find-bar");
    bar.classList.add("open");
    const input = document.getElementById("find-input");
    input.focus();
    input.select();
    if (input.value) runFind(input.value);
}

function closeFindBar() {
    document.getElementById("find-bar").classList.remove("open");
    clearFindHighlights();
    if (typeof textarea !== "undefined") textarea.focus();
}

function clearFindHighlights() {
    document.querySelectorAll(".find-match").forEach(m => {
        const text = document.createTextNode(m.textContent);
        m.parentNode.replaceChild(text, m);
    });
    document.querySelectorAll(".bubble").forEach(b => b.normalize());
    _findMatches = [];
    _findIndex = -1;
}

function runFind(query) {
    clearFindHighlights();
    if (!query) { updateFindCount(); return; }
    const q = query.toLowerCase();
    document.querySelectorAll(".bubble").forEach(b => highlightInNode(b, q));
    _findMatches = Array.from(document.querySelectorAll(".find-match"));
    _findIndex = _findMatches.length > 0 ? 0 : -1;
    setCurrentMatch();
    updateFindCount();
    if (_findIndex >= 0) scrollToMatch();
}

function highlightInNode(node, q) {
    if (node.nodeType === Node.TEXT_NODE) {
        const text = node.nodeValue;
        const lower = text.toLowerCase();
        let idx = lower.indexOf(q);
        if (idx === -1) return;
        const frag = document.createDocumentFragment();
        let cursor = 0;
        while (idx !== -1) {
            if (idx > cursor) frag.appendChild(document.createTextNode(text.substring(cursor, idx)));
            const mark = document.createElement("span");
            mark.className = "find-match";
            mark.textContent = text.substring(idx, idx + q.length);
            frag.appendChild(mark);
            cursor = idx + q.length;
            idx = lower.indexOf(q, cursor);
        }
        if (cursor < text.length) frag.appendChild(document.createTextNode(text.substring(cursor)));
        node.parentNode.replaceChild(frag, node);
    } else if (node.nodeType === Node.ELEMENT_NODE && !node.classList.contains("find-match")) {
        Array.from(node.childNodes).forEach(c => highlightInNode(c, q));
    }
}

function setCurrentMatch() {
    _findMatches.forEach((m, i) => m.classList.toggle("current", i === _findIndex));
}

function updateFindCount() {
    document.getElementById("find-count").textContent =
        _findMatches.length === 0 ? "0/0" : `${_findIndex + 1}/${_findMatches.length}`;
}

function scrollToMatch() {
    if (_findIndex < 0) return;
    _findMatches[_findIndex].scrollIntoView({ block: "center", behavior: "smooth" });
}

function findNext() {
    if (_findMatches.length === 0) return;
    _findIndex = (_findIndex + 1) % _findMatches.length;
    setCurrentMatch();
    updateFindCount();
    scrollToMatch();
}

function findPrev() {
    if (_findMatches.length === 0) return;
    _findIndex = (_findIndex - 1 + _findMatches.length) % _findMatches.length;
    setCurrentMatch();
    updateFindCount();
    scrollToMatch();
}

document.getElementById("find-input").addEventListener("input", e => runFind(e.target.value));
document.getElementById("find-input").addEventListener("keydown", e => {
    if (e.key === "Enter") {
        e.preventDefault();
        if (e.shiftKey) findPrev();
        else findNext();
    }
});

// ── Diff viewer ───────────────────────────────────────────────
function openDiffModal() {
    document.getElementById("diff-modal-overlay").classList.add("open");
    document.getElementById("diff-stat").textContent = "";
    document.getElementById("diff-body").innerHTML = '<span class="usage-loading">Running git diff…</span>';
    window.chrome.webview.postMessage({
        type: "get-diff",
        autoSave: autoSaveValues[+autoSaveSlider.value]
    });
}

function closeDiffModal() {
    document.getElementById("diff-modal-overlay").classList.remove("open");
}

let _diffFiles = [];
let _diffStat = "";
let _diffActive = null;

function renderDiff(stat, diff) {
    _diffStat = stat || "";
    _diffFiles = parseDiffFiles(diff || "");
    _diffActive = null;
    paintDiffStat();
    paintDiffBody();
}

function parseDiffFiles(diff) {
    if (!diff) return [];
    const out = [];
    const parts = diff.split(/(?=^diff --git )/m);
    for (const part of parts) {
        if (!part.trim()) continue;
        const m = part.match(/^diff --git a\/(\S+) b\/\S+/m);
        if (m) out.push({ name: m[1], body: part.trimEnd() });
    }
    return out;
}

function paintDiffStat() {
    const el = document.getElementById("diff-stat");
    el.innerHTML = "";
    if (!_diffStat) return;
    const lines = _diffStat.split("\n");
    for (const raw of lines) {
        const line = raw.replace(/^\s+/, "");
        if (!line) continue;
        const m = line.match(/^([^\s|]+)\s*\|/);
        const file = m && _diffFiles.find(f => f.name === m[1]);
        if (file) {
            const btn = document.createElement("div");
            btn.className = "diff-file-link" + (_diffActive === m[1] ? " active" : "");
            btn.textContent = line;
            btn.onclick = () => toggleDiffFilter(m[1]);
            el.appendChild(btn);
        } else {
            const span = document.createElement("div");
            span.className = "diff-stat-line";
            span.textContent = line;
            el.appendChild(span);
        }
    }
}

function toggleDiffFilter(name) {
    _diffActive = (_diffActive === name) ? null : name;
    paintDiffStat();
    paintDiffBody();
}

function paintDiffBody() {
    const body = document.getElementById("diff-body");
    body.innerHTML = "";
    if (_diffFiles.length === 0) {
        body.textContent = _diffStat ? "" : "No changes.";
        return;
    }
    const list = _diffActive ? _diffFiles.filter(f => f.name === _diffActive) : _diffFiles;
    for (let i = 0; i < list.length; i++) {
        if (i > 0) {
            const sep = document.createElement("div");
            sep.className = "diff-separator";
            body.appendChild(sep);
        }
        const pre = document.createElement("pre");
        const code = document.createElement("code");
        code.className = "language-diff";
        code.textContent = list[i].body;
        pre.appendChild(code);
        body.appendChild(pre);
        if (typeof hljs !== "undefined") hljs.highlightElement(code);
    }
}

renderCustomCommands();