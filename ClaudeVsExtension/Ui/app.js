console.log("Claude VS loaded");

const textarea = document.querySelector("textarea");
const sendButton = document.querySelector(".send");
const newChatButton = document.querySelector(".new-chat");
const modelSelect = document.querySelector(".model-select");
const effortSelect = document.querySelector(".effort-select");
const permissionSelect = document.querySelector(".permission-select");
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
    const text = await navigator.clipboard.readText();
    insertAtCursor(text);
});

document.getElementById("btn-file").addEventListener("click", () => {
    window.chrome.webview.postMessage({ type: "add-file" });
});

document.getElementById("btn-selection").addEventListener("click", () => {
    window.chrome.webview.postMessage({ type: "get-selection" });
});

textarea.addEventListener("keydown", (e) => {

    if (e.key === "Enter" && !e.shiftKey) {

        e.preventDefault();

        sendMessage();
    }
});

function sendMessage() {

    const text = textarea.value.trim();
    const activeAttachments = [...attachments.values()];

    if (!text && activeAttachments.length === 0)
        return;

    addMessage("user", text || `(${activeAttachments.map(a => a.displayName).join(", ")})`);

    let fullMessage = text;
    const filePaths = [];

    for (const att of activeAttachments) {
        if (att.content) {
            fullMessage += "\n\n" + att.content;
        } else if (att.filePath && att.includeFile) {
            filePaths.push(att.filePath);
        }
    }

    if (filePaths.length > 0)
        fullMessage += "\n\nFiles attached:\n" + filePaths.map(p => `  - ${p}`).join("\n");

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
    chip.className = "attachment-chip" + (isBinary ? " attachment-binary" : "");
    chip.dataset.attId = id;
    chip.innerHTML = `<span>${displayName}</span><button class="attachment-remove">×</button>`;
    chip.querySelector(".attachment-remove").addEventListener("click", () => {
        attachments.delete(id);
        chip.remove();
        document.getElementById(`q-card-${id}`)?.remove();
    });
    attachmentsEl.appendChild(chip);

    if (isBinary && filePath) {
        showFileQuestion(id, displayName);
    }
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
<button class="q-btn q-yes" onclick="confirmFile(${id}, true)">Sim</button>
<button class="q-btn q-no" onclick="confirmFile(${id}, false)">Não</button>
</div>`;
    messages.appendChild(card);
    messages.scrollTop = messages.scrollHeight;
}

function confirmFile(id, include) {
    const att = attachments.get(id);
    if (att) att.includeFile = include;

    const card = document.getElementById(`q-card-${id}`);
    if (card) {
        card.innerHTML = `<div class="question-text">📎 <strong>${escapeHtml(att?.displayName || "")}</strong> — ${include ? "✓ será enviado ao Claude" : "✗ ignorado"}</div>`;
        card.classList.add("question-answered");
    }

    if (!include) {
        attachments.delete(id);
        document.querySelector(`[data-att-id="${id}"]`)?.remove();
    } else {
        const chip = document.querySelector(`[data-att-id="${id}"]`);
        if (chip) chip.classList.remove("attachment-binary");
    }
}

function insertAtCursor(text) {
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    textarea.value = textarea.value.substring(0, start) + text + textarea.value.substring(end);
    textarea.selectionStart = textarea.selectionEnd = start + text.length;
    textarea.focus();
}

function escapeHtml(text) {

    const div = document.createElement("div");

    div.innerText = text;

    return div.innerHTML;
}