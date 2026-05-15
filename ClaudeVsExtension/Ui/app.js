console.log("Claude VS UI loaded");

document.querySelector(".send").addEventListener("click", () => {
    const text = document.querySelector("textarea").value;

    if (!text.trim()) {
        return;
    }

    alert("Mensagem enviada: " + text);
});