console.log("Claude VS loaded");

// ── Layout toggle ────────────────────────────────────────────
function toggleLayout() {
    const app = document.querySelector(".app");
    const btn = document.getElementById("btn-layout");
    app.classList.toggle("compact");
    btn.classList.toggle("active", app.classList.contains("compact"));
}

// ── Permission mode cycle ─────────────────────────────────────
const permissionCycle = ["auto", "plan", "ask"];
const permissionIcons = { auto: "⚡", plan: "📋", ask: "🔒" };
const permissionLabels = { auto: "auto — edits without asking", plan: "plan — plans only", ask: "ask — Claude default" };

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
    btn.classList.toggle("active", mode !== "auto");
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

function runCommand(cmd) {
    document.getElementById("cmd-menu").classList.remove("open");
    textarea.value = cmd;
    sendMessage();
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
const sendButton = document.querySelector(".send");
const newChatButton = document.querySelector(".new-chat");
const modelSelect = document.querySelector(".model-select");
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
effortSlider.addEventListener("input", () => {
    const i = +effortSlider.value;
    effortLabel.textContent = effortLabels[i];
    localStorage.setItem("effortLevel", i);
});
const permissionSelect = document.querySelector(".permission-select");
permissionSelect.addEventListener("change", updatePermissionBtn);
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

let attachments = new Map();
let attachmentIdCounter = 0;
let imageCounter = 0;
let currentStreamBubble = null;

sendButton.addEventListener("click", sendMessage);
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

    if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        hideAutocomplete();
        sendMessage();
    }
});

// ── /model picker ─────────────────────────────────────────────
const modelList = [
    { id: "claude-sonnet-4-6",         label: "Sonnet 4.6" },
    { id: "claude-opus-4-7",           label: "Opus 4.7" },
    { id: "claude-haiku-4-5-20251001", label: "Haiku 4.5" },
    { id: "claude-3-7-sonnet-20250219",label: "Sonnet 3.7" },
    { id: "claude-3-haiku-20240307",   label: "Haiku 3" },
];

function showModelPicker() {
    if (welcome) { welcome.remove(); welcome = null; }
    const current = modelSelect.value;
    const card = document.createElement("div");
    card.className = "question-card";
    card.innerHTML = `<div class="question-text">🤖 Escolha o modelo:</div>
        <div class="question-buttons" style="flex-wrap:wrap;gap:6px">
        ${modelList.map(m =>
            `<button class="q-btn${m.id === current ? " q-yes" : ""}" onclick="selectModel('${m.id}',this.closest('.question-card'))">${escapeHtml(m.label)}</button>`
        ).join("")}
        </div>`;
    messages.appendChild(card);
    messages.scrollTop = messages.scrollHeight;
}

function selectModel(id, card) {
    modelSelect.value = id;
    const label = modelSelect.options[modelSelect.selectedIndex]?.text || id;
    card.innerHTML = `<div class="question-text">🤖 Modelo: <strong>${escapeHtml(label)}</strong></div>`;
    card.classList.add("question-answered");
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
// Sempre roteia pelo C# — ele decide se é arquivo, imagem ou texto
textarea.addEventListener("paste", e => {
    e.preventDefault();
    window.chrome.webview.postMessage({ type: "get-clipboard-files" });
});

function sendMessage() {

    const text = textarea.value.trim();
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

    // Fecha cards pendentes não confirmados
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

    if (window.chrome?.webview) {
        addLoading();
        window.chrome.webview.postMessage({
            type: "chat",
            text: fullMessage,
            model: modelSelect.value,
            effort: effortSelect.value || null,
            permissionMode: permissionSelect.value
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

    messages.scrollTop = messages.scrollHeight;
}

window.chrome.webview.addEventListener("message", event => {

    if (event.data.type === "history") {
        renderHistory(event.data.sessions);
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
        appendChunk(event.data.text);
        return;
    }

    if (event.data.type === "timing") {
        appendTiming(event.data.text);
        return;
    }

    if (event.data.type === "stream-done") {
        currentStreamBubble = null;
        return;
    }
});

function appendTiming(text) {
    const mode = timingSelect.value;
    if (mode === "none") return;
    if (mode === "simple" && !text.startsWith("total:")) return;
    const el = document.createElement("div");
    el.className = "timing";
    el.textContent = `⏱ ${text}`;
    messages.appendChild(el);
    messages.scrollTop = messages.scrollHeight;
}

function appendChunk(text) {
    removeLoading();

    if (!currentStreamBubble) {
        const msg = document.createElement("div");
        msg.className = "message assistant";
        msg.innerHTML = `<div class="bubble"></div>`;
        messages.appendChild(msg);
        currentStreamBubble = msg.querySelector(".bubble");
    }

    currentStreamBubble.textContent += text;
    messages.scrollTop = messages.scrollHeight;
}

function addLoading() {
    const el = document.createElement("div");
    el.className = "message assistant loading";
    el.innerHTML = `<div class="bubble"><span class="dots"><span>.</span><span>.</span><span>.</span></span></div>`;
    messages.appendChild(el);
    messages.scrollTop = messages.scrollHeight;
}

function removeLoading() {
    const el = messages.querySelector(".loading");
    if (el) el.remove();
}

function clearChat() {
    messages.innerHTML = `
        <div class="welcome">
            <div class="hero"><span class="logo">✺</span> Claude VS</div>
            <div class="bot">🤖</div>
            <div class="hint">Type /model to pick the right tool for the job.</div>
        </div>`;

    welcome = messages.querySelector(".welcome");

    textarea.value = "";

    window.chrome.webview.postMessage({ type: "clear" });
}

function addAttachment(filename, content, isBinary, filePath) {
    const imageExts = ["png", "jpg", "jpeg", "gif", "bmp", "ico", "webp", "tiff"];
    const ext = filename.split(".").pop().toLowerCase();
    const isImage = imageExts.includes(ext);

    const displayName = isImage ? `imagem${++imageCounter}.${ext}` : filename;

    const id = attachmentIdCounter++;
    attachments.set(id, { displayName, content: isBinary ? null : content, filePath: filePath || null, includeFile: false });

    const chip = document.createElement("div");
    chip.className = "attachment-chip attachment-binary";
    chip.dataset.attId = id;
    chip.innerHTML = `<span>${displayName}</span><button class="attachment-remove">×</button>`;
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
<div class="question-text">📎 <strong>${escapeHtml(displayName)}</strong> — Deseja que o Claude leia este arquivo?</div>
<div class="question-buttons">
<button class="q-btn q-yes" onclick="confirmFile(${id}, true)">Yes</button>
<button class="q-btn q-no" onclick="confirmFile(${id}, false)">No</button>
</div>`;
    messages.appendChild(card);
    messages.scrollTop = messages.scrollHeight;
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
}

function renderHistory(sessions) {
    const list = document.getElementById("history-list");
    if (!sessions || sessions.length === 0) {
        list.innerHTML = '<div class="cmd-item" style="color:#555;font-family:inherit">No sessions found</div>';
        return;
    }
    list.innerHTML = sessions.map(s => `
<div class="cmd-item history-item" onclick="resumeSession('${escapeAttr(s.id)}')">
  <div class="history-preview">${escapeHtml(s.preview)}</div>
  <div class="history-date">${escapeHtml(s.date)}</div>
</div>`).join("");
}

function resumeSession(sessionId) {
    document.getElementById("history-menu").classList.remove("open");
    window.chrome.webview.postMessage({ type: "resume-session", sessionId });
}

function escapeHtml(text) {

    const div = document.createElement("div");

    div.innerText = text;

    return div.innerHTML;
}

renderCustomCommands();