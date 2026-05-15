console.log("Claude VS loaded");

const textarea = document.querySelector("textarea");
const sendButton = document.querySelector(".send");

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

    if (window.chrome?.webview) {

        window.chrome.webview.postMessage({
            type: "chat",
            text: text
        });
    }

    textarea.value = "";
}

window.chrome.webview.addEventListener("message", event => {

    console.log("Resposta do C#:", event.data);

    alert(event.data.text);
});