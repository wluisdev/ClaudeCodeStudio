console.log("Claude VS loaded");

const textarea = document.querySelector("textarea");
const sendButton = document.querySelector(".send");
const messages = document.querySelector("#messages");
const welcome = document.querySelector(".welcome");

sendButton.addEventListener("click", sendMessage);

textarea.addEventListener("keydown", (e) => {

    if (e.key === "Enter" && !e.shiftKey) {

        e.preventDefault();

        sendMessage();
    }
});

function sendMessage() {

    const text = textarea.value.trim();

    if (!text) {
        return;
    }

    addMessage("user", text);

    if (window.chrome?.webview) {

        window.chrome.webview.postMessage({
            type: "chat",
            text: text
        });
    }

    textarea.value = "";
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

    addMessage(
        "assistant",
        event.data.text);
});

function escapeHtml(text) {

    const div = document.createElement("div");

    div.innerText = text;

    return div.innerHTML;
}