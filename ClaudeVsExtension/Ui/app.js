console.log("Claude VS loaded");

const textarea = document.querySelector("textarea");
const sendButton = document.querySelector(".send");
const newChatButton = document.querySelector(".new-chat");
const modelSelect = document.querySelector(".model-select");
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
    for (const att of activeAttachments)
        if (att.content) fullMessage += "\n\n" + att.content;

    attachments.clear();
    attachmentsEl.innerHTML = "";
    textarea.value = "";

    if (window.chrome?.webview) {
        addLoading();
        window.chrome.webview.postMessage({
            type: "chat",
            text: fullMessage,
            model: modelSelect.value
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
        addAttachment(event.data.filename, event.data.content, event.data.isBinary);
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

function addAttachment(filename, content, isBinary) {
    const imageExts = ["png", "jpg", "jpeg", "gif", "bmp", "ico", "webp", "tiff"];
    const ext = filename.split(".").pop().toLowerCase();
    const isImage = imageExts.includes(ext);

    const displayName = isImage ? `imagem${++imageCounter}.${ext}` : filename;

    const id = attachmentIdCounter++;
    attachments.set(id, { displayName, content: isBinary ? null : content });

    const chip = document.createElement("div");
    chip.className = `attachment-chip${isBinary ? " attachment-binary" : ""}`;
    chip.title = isBinary ? "Arquivos binários não são enviados como conteúdo" : "";
    chip.innerHTML = `<span>${displayName}</span><button class="attachment-remove">×</button>`;
    chip.querySelector(".attachment-remove").addEventListener("click", () => {
        attachments.delete(id);
        chip.remove();
    });
    attachmentsEl.appendChild(chip);
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