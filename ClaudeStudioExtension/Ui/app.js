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

// Permission mode is selected via the composer-actions dropdown
// (.permission-select). The legacy topbar button + cycle/update helpers
// were removed once the inline dropdown carried that role.


// ── Settings panel ───────────────────────────────────────────
function toggleSettings() {
    const menu = document.getElementById("settings-menu");
    const opened = menu.classList.toggle("open");
    if (opened) {
        // Stale filters from the last visit would silently hide settings.
        const search = document.getElementById("settings-search");
        if (search) {
            if (search.value) { search.value = ""; filterSettings(); }
            search.focus();
        }
    }
}

function clearSettingsSearch() {
    const search = document.getElementById("settings-search");
    if (!search) return;
    search.value = "";
    filterSettings();
    search.focus();
}

// ── Settings search ──────────────────────────────────────────
// Filters ⚙ panel rows by name (label text + ⓘ tooltip). The menu is a flat
// list where control blocks (slider wraps, input wraps, timing options, cost
// limits) are SIBLINGS of the .settings-row that names them — they inherit
// the visibility of the row above. A section title that matches keeps its
// whole section; a section with no visible row hides its header. Dividers
// and the about block only show when no filter is active.
function filterSettings() {
    const menu = document.getElementById("settings-menu");
    if (!menu) return;
    const q = (document.getElementById("settings-search")?.value || "").trim().toLowerCase();
    const filtering = q.length > 0;

    let section = null;
    let sectionTitleHit = false;
    let sectionHasHit = false;
    let rowVisible = true;
    let anyHit = false;

    const flushSection = () => {
        if (section) section.style.display = (!filtering || sectionTitleHit || sectionHasHit) ? "" : "none";
        section = null;
        sectionTitleHit = false;
        sectionHasHit = false;
    };

    for (const el of menu.children) {
        const cl = el.classList;
        if (cl.contains("settings-search-wrap") || cl.contains("settings-search-empty")) continue;

        if (cl.contains("cmd-divider") || cl.contains("about-section")) {
            flushSection();
            el.style.display = filtering ? "none" : "";
            rowVisible = true;
            continue;
        }

        if (cl.contains("cmd-section")) {
            flushSection();
            section = el;
            sectionTitleHit = filtering && (el.textContent || "").toLowerCase().includes(q);
            if (sectionTitleHit) anyHit = true;
            continue;
        }

        if (cl.contains("settings-row")) {
            const label = el.querySelector(".settings-label");
            const name = ((label ? label.textContent : el.textContent) || "").toLowerCase();
            const hint = (label?.querySelector(".info-icon")?.title || "").toLowerCase();
            rowVisible = !filtering || sectionTitleHit || name.includes(q) || hint.includes(q);
            if (rowVisible) { sectionHasHit = true; anyHit = true; }
            el.style.display = rowVisible ? "" : "none";
            continue;
        }

        // Companion control block — follows the row that precedes it.
        el.style.display = rowVisible ? "" : "none";
    }
    flushSection();

    const empty = document.getElementById("settings-search-empty");
    if (empty) empty.hidden = !filtering || anyHit;
}

function openTrustedWorkspacesModal() {
    const overlay = document.getElementById("trusted-workspaces-overlay");
    if (!overlay) return;
    overlay.classList.add("open");
    try { window.chrome.webview.postMessage({ type: "get-trusted-workspaces" }); } catch (e) {}
}

function closeTrustedWorkspacesModal() {
    const overlay = document.getElementById("trusted-workspaces-overlay");
    if (overlay) overlay.classList.remove("open");
}

function renderTrustedWorkspacesList(paths) {
    const container = document.getElementById("trusted-workspaces-list");
    if (!container) return;
    if (!paths || paths.length === 0) {
        container.innerHTML = '<div class="trusted-workspaces-empty">No trusted workspaces yet.</div>';
        return;
    }
    container.innerHTML = "";
    for (const path of paths) {
        const row = document.createElement("div");
        row.className = "trusted-workspace-row";

        const label = document.createElement("span");
        label.className = "trusted-workspace-path";
        label.textContent = shortenTrustedPath(path);
        label.title = path;

        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "trusted-workspace-remove";
        remove.title = "Remove from trusted list";
        remove.textContent = "✕";
        remove.onclick = () => removeTrustedWorkspace(path);

        row.appendChild(label);
        row.appendChild(remove);
        container.appendChild(row);
    }
}

function shortenTrustedPath(full) {
    if (!full) return "";
    const parts = full.replace(/\\/g, "/").split("/").filter(Boolean);
    if (parts.length <= 2) return full;
    return "…/" + parts.slice(-2).join("/");
}

function removeTrustedWorkspace(path) {
    try { window.chrome.webview.postMessage({ type: "remove-trusted-workspace", path }); } catch (e) {}
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
    updateHistoryHint();
}

function updateHistoryHint() {
    const el = document.getElementById("history-hint");
    if (!el) return;
    if (historyIndex < 0) {
        el.hidden = true;
        el.textContent = "";
    } else {
        // Show 1-based "current/total" — older prompts have lower display index
        el.textContent = `↑ history ${historyIndex + 1}/${promptHistory.length}`;
        el.hidden = false;
    }
}

// ── Session history ───────────────────────────────────────────
let historySessions = [];
let historyScope = "all";
let historyWorkspaceName = null;

function toggleHistory() {
    const menu = document.getElementById("history-menu");
    const isOpen = menu.classList.toggle("open");
    if (isOpen) {
        document.getElementById("history-search").value = "";
        document.getElementById("history-list").innerHTML =
            '<div class="cmd-item" style="color:#555;font-family:inherit">Loading…</div>';
        requestHistory();
    }
}

function requestHistory() {
    const showAll = document.getElementById("history-show-all")?.checked || false;
    window.chrome.webview.postMessage({ type: "get-history", showAll });
}

function onShowAllToggle() {
    document.getElementById("history-list").innerHTML =
        '<div class="cmd-item" style="color:#555;font-family:inherit">Loading…</div>';
    requestHistory();
}

function filterHistory() {
    const q = document.getElementById("history-search").value.trim().toLowerCase();
    const filtered = q
        ? historySessions.filter(s =>
            (s.preview || "").toLowerCase().includes(q) ||
            (s.title || "").toLowerCase().includes(q))
        : historySessions;
    renderHistoryList(filtered, q);
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
    // Refresh discovered slash commands on every open — picks up new
    // .claude/commands files without a watcher.
    if (menu.classList.contains("open")) {
        try { window.chrome.webview.postMessage({ type: "get-slash-commands" }); } catch (e) {}
    }
}

// Renders the discovered .claude/commands entries (project + user scope) into
// the ⌘ menu. Clicking forwards "/name" to claude, which expands the command.
// Discovered custom commands (V9) also feed the composer's "/" autocomplete —
// refreshed whenever the ⌘ menu opens or the workspace changes.
let discoveredSlashCommands = [];

function renderSlashCommands(project, user, projectSkills, userSkills) {
    // Skills invoke like commands (the CLI expands "/<name>" for both), so
    // they join the "/" autocomplete pool; a project and a user skill with the
    // same name dedupe to one entry (most-specific wins, like the CLI).
    const skillNames = new Map();
    [...(projectSkills || []), ...(userSkills || [])].forEach(s => {
        if (!skillNames.has(s.name)) skillNames.set(s.name, s);
    });
    const skills = [...skillNames.values()];
    discoveredSlashCommands = [...(project || []), ...(user || []), ...skills].map(c => "/" + c.name);
    const fill = (wrapId, listId, cmds) => {
        const wrap = document.getElementById(wrapId);
        const list = document.getElementById(listId);
        if (!cmds || cmds.length === 0) { wrap.style.display = "none"; list.innerHTML = ""; return; }
        list.innerHTML = cmds.map(c => {
            const name = "/" + c.name;
            return `<div class="cmd-item" title="${escapeAttrValue(c.description || "")}" onclick="runCommand('${escapeAttr(name)}')">${escapeHtml(name)}</div>`;
        }).join("");
        wrap.style.display = "";
    };
    fill("project-cmds", "project-cmd-list", project);
    fill("user-cmds", "user-cmd-list", user);
    fill("skill-cmds", "skill-cmd-list", skills);
}

// ── Context usage (V12) ───────────────────────────────────────
// Asks the live claude for its context breakdown via the agent's
// get_context_usage control_request and renders it as a card in the chat.
let _pendingCtxUsageCard = null;

function requestContextUsage() {
    document.getElementById("cmd-menu").classList.remove("open");
    if (welcome) { welcome.remove(); welcome = null; }

    const card = document.createElement("div");
    card.className = "question-card ctx-usage-card";
    card.innerHTML = `<div class="question-text">◔ Context usage` +
        `<button type="button" class="pinned-close" title="Close — moves the card into the chat history">✕</button></div>` +
        `<div class="ctx-usage-body">Loading…</div>`;
    card.querySelector(".pinned-close").onclick = () => unpinCard(card);
    pinCard(card, messages, "manual");
    _pendingCtxUsageCard = card;

    try { window.chrome.webview.postMessage({ type: "get-context-usage" }); }
    catch (e) { card.querySelector(".ctx-usage-body").textContent = "Failed to reach the extension."; }
}

// ── Side question (V19) ───────────────────────────────────────
// Answered with the session's context but kept out of the transcript — the
// card is UI-only; the session JSONL records nothing.
let _pendingSideQuestionCard = null;

function openSideQuestionCard() {
    document.getElementById("cmd-menu").classList.remove("open");
    if (welcome) { welcome.remove(); welcome = null; }

    const card = document.createElement("div");
    card.className = "question-card side-question-card";
    card.innerHTML = `<div class="question-text">💬 Side question <span class="side-question-note">answered with the session's context — not added to the transcript</span></div>
        <div class="side-question-ask">
            <input type="text" placeholder="e.g. what did that error mean?"
                onkeydown="if(event.key==='Enter'){event.preventDefault();sendSideQuestion(this.closest('.side-question-card'));}" />
            <button type="button" onclick="sendSideQuestion(this.closest('.side-question-card'))">Ask</button>
        </div>
        <div class="side-question-answer" style="display:none"></div>`;
    messages.appendChild(card);
    autoScroll();
    card.querySelector("input").focus();
}

function sendSideQuestion(card) {
    const input = card.querySelector("input");
    const q = (input.value || "").trim();
    if (!q) return;

    input.disabled = true;
    card.querySelector(".side-question-ask button").disabled = true;
    const answerEl = card.querySelector(".side-question-answer");
    answerEl.style.display = "";
    answerEl.textContent = "Thinking…";
    _pendingSideQuestionCard = card;

    try { window.chrome.webview.postMessage({ type: "side-question", text: q }); }
    catch (e) { answerEl.textContent = "Failed to reach the extension."; }
    autoScroll();
}

function renderSideQuestionAnswer(answer, error) {
    const card = _pendingSideQuestionCard;
    _pendingSideQuestionCard = null;
    if (!card || !document.body.contains(card)) return;
    const answerEl = card.querySelector(".side-question-answer");

    if (error) {
        const friendly = error.includes("no active session") || error.includes("agent not running")
            ? "No active session yet — send a message first, then ask again."
            : `Failed: ${error}`;
        answerEl.textContent = friendly;
    } else if (!answer) {
        answerEl.textContent = "Claude declined to answer this one.";
    } else {
        answerEl.innerHTML = renderMarkdown(answer);
    }
    autoScroll();
}

// ── MCP status + reconnect (V20) ──────────────────────────────
let _pendingMcpCard = null;

function requestMcpStatus() {
    document.getElementById("cmd-menu").classList.remove("open");
    if (welcome) { welcome.remove(); welcome = null; }

    // Reuse the open card when refreshing after a reconnect.
    let card = _pendingMcpCard && document.body.contains(_pendingMcpCard)
        ? _pendingMcpCard
        : null;
    if (!card) {
        card = document.createElement("div");
        card.className = "question-card mcp-status-card";
        card.innerHTML = `<div class="question-text">🔌 MCP status</div><div class="mcp-status-body">Loading…</div>`;
        messages.appendChild(card);
        autoScroll();
    }
    _pendingMcpCard = card;

    try { window.chrome.webview.postMessage({ type: "get-mcp-status" }); }
    catch (e) { card.querySelector(".mcp-status-body").textContent = "Failed to reach the extension."; }
}

function reconnectMcpServer(name) {
    if (_pendingMcpCard) {
        const row = _pendingMcpCard.querySelector(`.mcp-status-row[data-server="${CSS.escape(name)}"] .mcp-reconnect-btn`);
        if (row) { row.disabled = true; row.textContent = "Reconnecting…"; }
    }
    try { window.chrome.webview.postMessage({ type: "mcp-reconnect", text: name }); } catch (e) {}
}

function renderMcpStatusCard(serversJson, error) {
    const card = _pendingMcpCard;
    if (!card || !document.body.contains(card)) { _pendingMcpCard = null; return; }
    const body = card.querySelector(".mcp-status-body");

    if (error || !serversJson) {
        const friendly = (error || "").includes("no active session") || (error || "").includes("agent not running")
            ? "No active session yet — send a message first, then check again."
            : `Failed to load MCP status: ${error || "empty response"}`;
        body.textContent = friendly;
        return;
    }

    let servers;
    try { servers = JSON.parse(serversJson); }
    catch (e) { body.textContent = "Failed to parse MCP status."; return; }
    if (!Array.isArray(servers) || servers.length === 0) {
        body.textContent = "No MCP servers in this session.";
        return;
    }

    body.innerHTML = servers.map(s => {
        const name = s.name || "?";
        const status = String(s.status || s.state || "unknown");
        const ok = /connected|ready|ok/i.test(status);
        return `<div class="mcp-status-row" data-server="${escapeAttr(name)}">
  <span class="mcp-status-dot ${ok ? "mcp-ok" : "mcp-bad"}"></span>
  <span class="mcp-status-name">${escapeHtml(name)}</span>
  <span class="mcp-status-state">${escapeHtml(status)}</span>
  <button type="button" class="mcp-reconnect-btn" onclick="reconnectMcpServer('${escapeAttr(name)}')">Reconnect</button>
</div>`;
    }).join("");
    autoScroll();
}

const _ctxPalette = ["#d97757", "#6a9bcc", "#8a7fd0", "#5faa8a", "#c9a75a", "#c47ba6", "#7fb6c9", "#a0a68a"];

function fmtTokens(n) {
    if (n >= 1000000) return (n / 1000000).toFixed(1) + "M";
    if (n >= 1000) return (n / 1000).toFixed(1) + "k";
    return String(n || 0);
}

function renderContextUsageCard(usageJson, error) {
    const card = _pendingCtxUsageCard;
    _pendingCtxUsageCard = null;
    if (!card) return;
    const body = card.querySelector(".ctx-usage-body");

    if (error || !usageJson) {
        const friendly = (error || "").includes("no active session") || (error || "").includes("agent not running")
            ? "No active session yet — send a message first, then check again."
            : `Failed to load context usage: ${error || "empty response"}`;
        body.textContent = friendly;
        return;
    }

    let u;
    try { u = JSON.parse(usageJson); }
    catch (e) { body.textContent = "Failed to parse context usage."; return; }

    const cats = (u.categories || []).filter(c => (c.tokens || 0) > 0 && !c.isDeferred);
    const segs = cats.filter(c => c.name !== "Free space");
    const max = u.rawMaxTokens || 1;

    let html = `<div class="ctx-usage-header"><span>${escapeHtml(u.model || "")}</span>` +
        `<span>${fmtTokens(u.totalTokens)} / ${fmtTokens(u.rawMaxTokens)} tokens (${u.percentage ?? "?"}%)</span></div>`;

    html += `<div class="ctx-usage-track">` + segs.map(c =>
        `<div class="ctx-usage-seg" style="background:${_ctxPalette[cats.indexOf(c) % _ctxPalette.length]};width:${(c.tokens / max * 100).toFixed(2)}%" title="${escapeAttrValue(c.name)}: ${fmtTokens(c.tokens)}"></div>`
    ).join("") + `</div>`;

    html += `<div class="ctx-cat-list">` + cats.map((c, i) => {
        const swatch = c.name === "Free space" ? "" : `background:${_ctxPalette[i % _ctxPalette.length]}`;
        const pct = ((c.tokens / max) * 100).toFixed(1);
        return `<div class="ctx-cat-row"><span class="ctx-cat-swatch" style="${swatch}"></span>` +
            `<span class="ctx-cat-name">${escapeHtml(c.name)}</span>` +
            `<span class="ctx-cat-tokens">${fmtTokens(c.tokens)}</span>` +
            `<span class="ctx-cat-pct">${pct}%</span></div>`;
    }).join("") + `</div>`;

    const top = (arr, nameOf) => (arr || []).slice().sort((a, b) => (b.tokens || 0) - (a.tokens || 0)).slice(0, 5)
        .map(x => `<div class="ctx-cat-row"><span class="ctx-cat-swatch"></span>` +
            `<span class="ctx-cat-name">${escapeHtml(nameOf(x))}</span>` +
            `<span class="ctx-cat-tokens">${fmtTokens(x.tokens)}</span><span class="ctx-cat-pct"></span></div>`).join("");

    if ((u.memoryFiles || []).length > 0)
        html += `<div class="ctx-usage-subheader">Memory files</div><div class="ctx-cat-list">${top(u.memoryFiles, x => x.path || "")}</div>`;
    if ((u.agents || []).length > 0)
        html += `<div class="ctx-usage-subheader">Custom agents</div><div class="ctx-cat-list">${top(u.agents, x => x.agentType || "")}</div>`;

    body.innerHTML = html;
    autoScroll();
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
        // Synthetic replay hosts have no text of their own — their textContent
        // is chip labels, which would export as a garbage Claude turn.
        if (msg.classList.contains("replay-tool-host")) continue;
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
// Restore the last chosen model (survives VS restarts). A stored id that no
// longer exists in the list (renamed/removed model) is ignored — setting an
// unknown value would leave the select on the default anyway.
(function restoreModelSelection() {
    const stored = localStorage.getItem("chatModel");
    if (stored && [...modelSelect.options].some(o => o.value === stored))
        modelSelect.value = stored;
})();
// Tracks which model is currently in effect, so noteModelSwitch() can tell a
// real change from a no-op and drop a divider in the transcript at the point
// the model changed. Seeded with the initial selection (no divider at startup).
let activeModelId = modelSelect.value;

// ── Trust MCP servers modal ────────────────────────────────
let _mcpTrustPending = [];  // [{name, scope, transport, summary, hash}]

function openMcpTrustModal(servers) {
    if (!servers || servers.length === 0) return;
    _mcpTrustPending = servers.slice();

    const overlay = document.getElementById("mcp-trust-modal-overlay");
    if (!overlay) return;

    mcpTrustRenderList();
    const selectAll = document.getElementById("mcp-trust-select-all");
    if (selectAll) selectAll.checked = true;
    overlay.classList.add("open");
}

function mergeMcpTrustModal(servers) {
    if (!servers || servers.length === 0) return;
    const seen = new Set(_mcpTrustPending.map(mcpTrustKey));
    for (const s of servers) {
        if (!seen.has(mcpTrustKey(s))) _mcpTrustPending.push(s);
    }
    mcpTrustRenderList();
    mcpTrustUpdateSelectAllState();
}

function mcpTrustKey(s) {
    return `${s.scope || "user"}\0${s.name || ""}\0${s.hash || ""}\0${s.projectPath || ""}`;
}

function mcpTrustRenderList() {
    const list = document.getElementById("mcp-trust-list");
    if (!list) return;
    list.innerHTML = "";
    _mcpTrustPending.forEach((s, idx) => {
        const item = document.createElement("label");
        item.className = "mcp-trust-item";

        const body = document.createElement("div");
        body.className = "mcp-trust-item-body";

        const head = document.createElement("div");
        head.className = "mcp-trust-item-head";

        const name = document.createElement("span");
        name.className = "mcp-trust-item-name";
        name.textContent = s.name;
        head.appendChild(name);

        const scopeChip = document.createElement("span");
        scopeChip.className = "mcp-trust-chip mcp-trust-chip-scope";
        scopeChip.textContent = s.scope || "user";
        head.appendChild(scopeChip);

        const transportChip = document.createElement("span");
        transportChip.className = "mcp-trust-chip mcp-trust-chip-transport";
        transportChip.textContent = s.transport || "stdio";
        head.appendChild(transportChip);

        body.appendChild(head);

        const summary = document.createElement("div");
        summary.className = "mcp-trust-item-summary";
        summary.textContent = s.summary || "";
        summary.title = s.summary || "";
        body.appendChild(summary);

        item.appendChild(body);

        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = true;
        cb.dataset.idx = String(idx);
        cb.addEventListener("change", mcpTrustUpdateSelectAllState);
        item.appendChild(cb);

        list.appendChild(item);
    });
}

function closeMcpTrustModal() {
    const overlay = document.getElementById("mcp-trust-modal-overlay");
    if (overlay) overlay.classList.remove("open");
    _mcpTrustPending = [];
}

function mcpTrustToggleAll(checked) {
    const list = document.getElementById("mcp-trust-list");
    if (!list) return;
    list.querySelectorAll('input[type="checkbox"]').forEach(cb => cb.checked = checked);
}

function mcpTrustUpdateSelectAllState() {
    const list = document.getElementById("mcp-trust-list");
    const selectAll = document.getElementById("mcp-trust-select-all");
    if (!list || !selectAll) return;
    const boxes = list.querySelectorAll('input[type="checkbox"]');
    const total = boxes.length;
    const checked = Array.from(boxes).filter(cb => cb.checked).length;
    selectAll.checked = checked === total;
    selectAll.indeterminate = checked > 0 && checked < total;
}

function mcpTrustSubmit() {
    const list = document.getElementById("mcp-trust-list");
    if (!list) return closeMcpTrustModal();
    const decisions = [];
    list.querySelectorAll('input[type="checkbox"]').forEach(cb => {
        const idx = parseInt(cb.dataset.idx, 10);
        const s = _mcpTrustPending[idx];
        if (!s) return;
        decisions.push({
            name: s.name,
            scope: s.scope,
            hash: s.hash,
            projectPath: s.projectPath || null,
            action: cb.checked ? "trust" : "skip"
        });
    });
    window.chrome.webview.postMessage({ type: "trust-mcp-servers", servers: decisions });
}

function mcpTrustCancel() {
    // Skip everything for this session — silences the prompt loop without trusting anything.
    const skips = _mcpTrustPending.map(s => ({
        name: s.name, scope: s.scope, hash: s.hash,
        projectPath: s.projectPath || null,
        action: "skip"
    }));
    if (skips.length > 0) {
        window.chrome.webview.postMessage({ type: "trust-mcp-servers", servers: skips });
    } else {
        closeMcpTrustModal();
    }
}
// ───────────────────────────────────────────────────────────

// ── Trust workspace modal ──────────────────────────────────
let _trustPendingPath = "";
let _trustPendingParent = "";

const TRUST_RISK_MESSAGES = {
    "drive-root": "Trusting this folder grants Claude access to the entire drive — Windows, Program Files, every existing and future folder on it.",
    "users-container": "Trusting this folder grants Claude access to every user profile on this machine.",
};

function openTrustModal(path, parent, parentIsBlocked, riskWarning) {
    if (!path) return;
    _trustPendingPath = path;
    _trustPendingParent = parent || "";

    const overlay = document.getElementById("trust-modal-overlay");
    const pathEl = document.getElementById("trust-modal-path");
    const parentRow = document.getElementById("trust-modal-parent-row");
    const parentPath = document.getElementById("trust-modal-parent-path");
    const parentBtn = document.getElementById("trust-parent-btn");
    const warningEl = document.getElementById("trust-modal-warning");
    const warningText = document.getElementById("trust-modal-warning-text");
    if (!overlay) return;

    pathEl.textContent = path;
    pathEl.title = path;

    // Hide "Trust parent" when the parent is a wide-trust trap (home, drive root,
    // C:\Users). Clicking it would prefix-trust an exponentially larger surface
    // via IsTrusted's prefix match — same reason home is blocked outright.
    const showParent = _trustPendingParent && _trustPendingParent !== path && !parentIsBlocked;
    if (showParent) {
        parentRow.hidden = false;
        parentPath.textContent = _trustPendingParent;
        const parentName = _trustPendingParent.split(/[\\/]+/).filter(Boolean).pop() || _trustPendingParent;
        parentBtn.textContent = `Trust parent (${parentName})`;
        parentBtn.title = `Trust ${_trustPendingParent}`;
        parentBtn.style.display = "";
    } else {
        parentRow.hidden = true;
        parentBtn.style.display = "none";
    }

    // Surface a high-risk warning when the user is about to trust a drive root
    // or C:\Users — both legitimate choices in rare cases (test VMs, sysadmin)
    // but catastrophic in normal use. Decision stays with the user.
    if (warningEl && warningText) {
        const msg = riskWarning ? TRUST_RISK_MESSAGES[riskWarning] : null;
        if (msg) {
            warningText.textContent = msg;
            warningEl.hidden = false;
        } else {
            warningEl.hidden = true;
            warningText.textContent = "";
        }
    }

    overlay.classList.add("open");
}

function closeTrustModal() {
    const overlay = document.getElementById("trust-modal-overlay");
    if (overlay) overlay.classList.remove("open");
    _trustPendingPath = "";
    _trustPendingParent = "";
}

function trustAllow() {
    if (!_trustPendingPath) return closeTrustModal();
    window.chrome.webview.postMessage({ type: "trust-workspace", path: _trustPendingPath });
    closeTrustModal();
}

function trustParent() {
    if (!_trustPendingParent) return closeTrustModal();
    window.chrome.webview.postMessage({ type: "trust-workspace", path: _trustPendingParent });
    closeTrustModal();
}

function trustDeny() {
    if (_trustPendingPath) {
        window.chrome.webview.postMessage({ type: "untrust-workspace", path: _trustPendingPath });
    }
    closeTrustModal();
}
// ───────────────────────────────────────────────────────────

// ── Cwd bar ────────────────────────────────────────────────
let _cwdVsPath = "";    // resolved from VS (Solution / Open Folder), via C#
let _cwdFullPath = "";  // effective cwd shown / copied (override wins when set)
let _workspaceTrusted = true;  // updated by trust-required / workspace-trusted events

// ── Working directory overrides (D6: per solution) ─────────
// Overrides live in a map keyed by the VS-resolved workspace path, so switching
// solutions never carries another solution's override along. The old global
// "workingDirectory" key is migrated under the first workspace seen (cwd-info)
// and removed; until then it still answers as a fallback.
function workingDirKey() {
    const p = (_cwdVsPath || "").trim().replace(/[\\/]+$/, "").toLowerCase();
    return p || "(none)";
}

function loadWorkingDirOverrides() {
    try { return JSON.parse(localStorage.getItem("workingDirOverrides") || "{}") || {}; }
    catch (e) { return {}; }
}

function getWorkingDirOverride() {
    const map = loadWorkingDirOverrides();
    const val = map[workingDirKey()];
    if (val !== undefined) return val;
    return (localStorage.getItem("workingDirectory") || "").trim();
}

function setWorkingDirOverride(value) {
    const map = loadWorkingDirOverrides();
    const v = (value || "").trim();
    if (v) map[workingDirKey()] = v;
    else delete map[workingDirKey()];
    localStorage.setItem("workingDirOverrides", JSON.stringify(map));
    localStorage.removeItem("workingDirectory");
}

function migrateLegacyWorkingDir() {
    const legacy = localStorage.getItem("workingDirectory");
    if (legacy === null) return;
    const v = legacy.trim();
    const map = loadWorkingDirOverrides();
    if (v && map[workingDirKey()] === undefined) {
        map[workingDirKey()] = v;
        localStorage.setItem("workingDirOverrides", JSON.stringify(map));
    }
    localStorage.removeItem("workingDirectory");
}

function truncateCwdPath(full) {
    if (!full) return "";
    const parts = full.split(/[\\/]+/).filter(Boolean);
    if (parts.length === 0) return full;
    // Two last segments when available, single when not — matches IDE conventions.
    return parts.length >= 2
        ? parts[parts.length - 2] + "\\" + parts[parts.length - 1]
        : parts[parts.length - 1];
}

function renderCwd(vsPath) {
    if (vsPath !== undefined) _cwdVsPath = vsPath || "";
    const override = getWorkingDirOverride();

    const bar = document.getElementById("cwdbar");
    const pathEl = document.getElementById("cwdbar-path");
    if (!bar || !pathEl) return;

    // Override wins when set; otherwise the VS-resolved path.
    const effective = override || _cwdVsPath;
    _cwdFullPath = effective;

    bar.classList.remove("cwdbar-override");

    if (!effective) {
        bar.classList.add("cwdbar-empty");
        bar.title = "No workspace open";
        pathEl.innerHTML = "";
        pathEl.textContent = "No workspace";
        return;
    }

    bar.classList.remove("cwdbar-empty");
    bar.classList.toggle("cwdbar-untrusted", !_workspaceTrusted);

    const isOverride = override && override !== _cwdVsPath;
    if (isOverride) bar.classList.add("cwdbar-override");

    let tooltip = effective;
    if (isOverride) tooltip += `  (override — VS workspace: ${_cwdVsPath || "none"})`;
    if (!_workspaceTrusted) tooltip += "  · not trusted";
    bar.title = tooltip;

    pathEl.innerHTML = "";
    pathEl.appendChild(document.createTextNode(truncateCwdPath(effective)));
    if (isOverride) {
        const tag = document.createElement("span");
        tag.className = "cwdbar-override-tag";
        tag.textContent = " (override)";
        pathEl.appendChild(tag);
    }
    if (!_workspaceTrusted) {
        const tag = document.createElement("span");
        tag.className = "cwdbar-untrusted-tag";
        tag.textContent = " · not trusted";
        pathEl.appendChild(tag);
    }
}

// ── Presence (U4) ──────────────────────────────────────────
// Live status from the CLI's presence file (~/.claude/sessions/<pid>.json).
// Only "waiting" states carry information the UI doesn't already show
// (streaming/pending are covered elsewhere), so that's all we render.
// Probed 2026-07-18 (2.1.205 and 2.1.214): the sdk-cli entrypoint never
// stamps status/waitingFor — the file watcher stays as future-proofing, and
// the waiting states are synthesized by the UI itself (modal/question open).
function renderPresence(status, waitingFor) {
    const el = document.getElementById("cwdbar-presence");
    if (!el) return;
    if (status === "waiting" && waitingFor) {
        el.textContent = "⏸ " + waitingFor;
        el.hidden = false;
    } else {
        el.textContent = "";
        el.hidden = true;
    }
}

function copyCwd() {
    if (!_cwdFullPath) return;
    // Untrusted workspace: reopen the trust prompt instead of copying — more useful
    // than silently copying a path the user can't actually act on yet. Round-trip
    // through the backend so parentIsHome is computed authoritatively (JS has no
    // access to %USERPROFILE%).
    if (!_workspaceTrusted) {
        try { window.chrome.webview.postMessage({ type: "refresh-cwd" }); } catch (e) {}
        return;
    }
    const bar = document.getElementById("cwdbar");
    const done = () => {
        if (!bar) return;
        bar.classList.add("cwdbar-copied");
        setTimeout(() => bar.classList.remove("cwdbar-copied"), 600);
    };
    try {
        navigator.clipboard.writeText(_cwdFullPath).then(done, done);
    } catch (e) {
        // Fallback for older WebView2 builds without async clipboard.
        const ta = document.createElement("textarea");
        ta.value = _cwdFullPath;
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand("copy"); } catch (_) {}
        document.body.removeChild(ta);
        done();
    }
}
// ────────────────────────────────────────────────────────────

let _accountInfo = null;
function renderAccountInfo() {
    const el = document.getElementById("account-info");
    if (!el) return;
    el.classList.remove("account-info-loading");

    if (!_accountInfo || !_accountInfo.signedIn) {
        el.textContent = "Not signed in";
        el.title = "Click to sign in with Claude";
        el.classList.add("account-info-muted");
        el.classList.add("account-info-clickable");
        el.onclick = startClaudeLogin;
        return;
    }
    el.classList.remove("account-info-muted");
    el.classList.remove("account-info-clickable");
    el.onclick = null;

    const customName = (localStorage.getItem("displayName") || "").trim();
    const accountName = _accountInfo.accountDisplayName || "";
    const org = _accountInfo.organizationName || "";
    const email = _accountInfo.email || "";
    const source = localStorage.getItem("accountInfoSource") || "auto";

    let name = "";
    let tooltip = "";

    if (source === "account") {
        name = accountName || "—";
        tooltip = accountName ? (email ? `${accountName} (${email})` : accountName) : "Account name unavailable";
    } else if (source === "organization") {
        name = org || "—";
        tooltip = org || "Organization unavailable";
    } else if (source === "email") {
        name = email || "—";
        tooltip = email || "Email unavailable";
    } else if (source === "custom") {
        name = customName || "—";
        tooltip = customName || "Set a custom name in settings";
    } else {
        // auto cascade: custom → account → organization → email → Anonymous
        if (customName) {
            name = customName;
            tooltip = email || customName;
        } else if (accountName) {
            name = accountName;
            tooltip = email ? `${accountName} (${email})` : accountName;
        } else if (org) {
            name = org;
            tooltip = email ? `${org} (${email})` : org;
        } else if (email) {
            name = email;
            tooltip = email;
        } else {
            name = "Anonymous";
            tooltip = "";
        }
    }

    const plan = _accountInfo.plan || "";
    el.innerHTML = "";
    const nameSpan = document.createElement("span");
    nameSpan.textContent = name;
    el.appendChild(nameSpan);
    if (plan) {
        const sep = document.createTextNode(" · ");
        el.appendChild(sep);
        const planSpan = document.createElement("span");
        planSpan.className = "account-info-plan";
        planSpan.textContent = plan;
        el.appendChild(planSpan);
    }
    el.title = tooltip;
}

// Tool-window caption attention markers (V13): "pending" while a permission
// modal or AskUserQuestion card waits on the user, "done" when a turn finishes
// while the tool window is hidden. The tab caption is the only surface that
// stays visible when the window is backgrounded — the in-chat .tool-pending
// chip isn't. Cleared on resolve / when the window regains visibility.
let _captionAttention = null;
let _toolWindowVisible = true;

function setCaptionAttention(state) {
    if (_captionAttention === state) return;
    _captionAttention = state;
    updateCaption();
    // Notification sounds (D3) ride the same transitions: "pending" fires
    // whenever claude blocks on the user; "done" is only ever set while the
    // window is hidden, matching the "turn ends unseen" semantics.
    if (state === "pending" && localStorage.getItem("soundOnInput") === "true") playSound("attention");
    if (state === "done" && localStorage.getItem("soundOnDone") === "true") playSound("done");
}

function playSound(sound) {
    try { window.chrome.webview.postMessage({ type: "play-sound", text: sound }); } catch (e) {}
}

function updateCaption() {
    const opt = modelSelect.options[modelSelect.selectedIndex];
    const label = opt?.text || modelSelect.value;
    modelSelect.title = opt?.title || modelSelect.value;
    const prefix = _captionAttention === "pending" ? "🔔 "
        : _captionAttention === "done" ? "✓ " : "";
    try {
        window.chrome.webview.postMessage({ type: "set-caption", text: `${prefix}Claude Code Studio — ${label}` });
    } catch (e) { /* webview not ready yet */ }
}
modelSelect.addEventListener("change", updateCaption);
window.addEventListener("DOMContentLoaded", updateCaption);
updateCaption();

// Drops a marker into the transcript when the model changes, so it's clear
// which model produced the messages above vs below the line. No-op when nothing
// actually changed, or before the conversation has started (a divider above an
// empty chat is just noise). Consecutive switches with no message in between
// collapse into one divider that's updated in place.
function noteModelSwitch() {
    const newId = modelSelect.value;
    if (newId === activeModelId) return;
    activeModelId = newId;
    localStorage.setItem("chatModel", newId);

    // Nothing above the line to attribute to the old model yet — skip.
    if (!messages.querySelector(".message")) return;

    const label = modelSelect.options[modelSelect.selectedIndex]?.text || newId;
    const text = `🤖 Switched to ${label}`;

    const last = messages.lastElementChild;
    if (last && last.classList.contains("model-divider")) {
        last.querySelector(".model-divider-label").textContent = text;
        autoScroll();
        return;
    }

    const div = document.createElement("div");
    div.className = "model-divider";
    div.innerHTML = `<span class="model-divider-label"></span>`;
    div.querySelector(".model-divider-label").textContent = text;
    messages.appendChild(div);
    autoScroll();
}
modelSelect.addEventListener("change", noteModelSwitch);

// ── Model picker (titlebar) ──────────────────────────────────
// Effort-style button + popup replacing the OS-rendered <select> dropdown so
// the model pick matches the effort/permission controls (rounded popup, item
// highlight). The hidden native select stays the single source of truth;
// picking an item writes it and dispatches "change" so every existing
// listener (caption, transcript divider, this button's label) runs unchanged.
const modelControl = document.getElementById("model-control");
const modelPopup = document.getElementById("model-popup");
const modelBtn = document.getElementById("model-btn");
const modelBtnLabel = document.getElementById("model-btn-label");

function toggleModelPopup() {
    if (modelPopup.hidden) buildModelPopup();
    modelPopup.hidden = !modelPopup.hidden;
}

function buildModelPopup() {
    modelPopup.innerHTML = "";
    [...modelSelect.options].forEach(opt => {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "permission-item" + (opt.value === modelSelect.value ? " active" : "");
        btn.title = opt.title || opt.value;
        const span = document.createElement("span");
        span.textContent = opt.text;
        btn.appendChild(span);
        btn.onclick = () => {
            modelPopup.hidden = true;
            if (opt.value === modelSelect.value) return;
            modelSelect.value = opt.value;
            modelSelect.dispatchEvent(new Event("change"));
        };
        modelPopup.appendChild(btn);
    });
}

function refreshModelButton() {
    const opt = modelSelect.options[modelSelect.selectedIndex];
    modelBtnLabel.textContent = opt?.text || modelSelect.value;
    modelBtn.title = opt?.title || modelSelect.value;
}
modelSelect.addEventListener("change", refreshModelButton);
refreshModelButton();

// Click outside closes; capture phase, same as the other popups.
document.addEventListener("click", (e) => {
    if (modelPopup.hidden) return;
    if (!modelControl.contains(e.target)) modelPopup.hidden = true;
}, true);

// Effort selector: compact button in the composer-actions row that opens a
// popup with a slider above it. Avoids the native <select> dropdown direction
// flipping (5 options need ~120px vertical and the composer sits at the
// bottom of the tool window, so the browser keeps wanting to open upward
// while the 3-option permission select stays downward — visual inconsistency).
const effortValues = ["", "low", "medium", "high", "xhigh", "max"];
const effortLabels = ["auto", "low", "med", "high", "xhigh", "max"];
const effortIcons  = ["🧭", "🪶", "⚙", "🧠", "🚀", "🔥"];
const effortBtn = document.getElementById("effort-btn");
const effortBtnIcon = document.getElementById("effort-btn-icon");
const effortBtnLabel = document.getElementById("effort-btn-label");
const effortPopup = document.getElementById("effort-popup");
const effortPopupSlider = document.getElementById("effort-popup-slider");
const effortControl = document.getElementById("effort-control");

// Shim so the existing send code (`effort: effortSelect.value || null`) still works.
const effortSelect = { get value() { return effortValues[+effortPopupSlider.value]; } };

(function initEffort() {
    // Migrate legacy index-based key (effortLevel) or string-based key (effortValue)
    // to the slider position.
    let idx = 0;
    const storedIdx = localStorage.getItem("effortLevel");
    const storedVal = localStorage.getItem("effortValue");
    if (storedIdx != null) {
        const n = parseInt(storedIdx, 10);
        if (!Number.isNaN(n) && n >= 0 && n < effortValues.length) idx = n;
    } else if (storedVal != null) {
        const found = effortValues.indexOf(storedVal);
        if (found >= 0) idx = found;
    }
    // "max" is session-only (D8) — a value persisted by pre-D8 builds would
    // otherwise keep max burning across restarts forever. Load as xhigh.
    if (effortValues[idx] === "max") {
        idx = effortValues.indexOf("xhigh");
        localStorage.setItem("effortLevel", idx);
    }
    effortPopupSlider.value = idx;
    updateEffortButton(idx);
})();

function updateEffortButton(idx) {
    effortBtnIcon.textContent = effortIcons[idx];
    effortBtnLabel.textContent = effortLabels[idx];
}

function toggleEffortPopup() {
    effortPopup.hidden = !effortPopup.hidden;
}

effortPopupSlider.addEventListener("input", () => {
    const idx = +effortPopupSlider.value;
    updateEffortButton(idx);
    // "max" is session-only (D8, política do dliedke v43): it never persists,
    // so a VS restart falls back to the last durable level instead of
    // silently keeping max effort burning across sessions.
    if (effortValues[idx] !== "max") localStorage.setItem("effortLevel", idx);
});

// Click outside the effort control closes the popup. Capture phase so we
// catch the click before it lands on inner buttons.
document.addEventListener("click", (e) => {
    if (effortPopup.hidden) return;
    if (!effortControl.contains(e.target)) effortPopup.hidden = true;
}, true);
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

// Effort is now driven by the dropdown next to permission-select in the
// composer-actions row (see effortSelect setup above) — the old slider
// listener that used to live here was removed when the UI moved.

// Permission mode: same button+popup pattern as effort. Categorical (not a
// slider), so the popup is a list of clickable items. The send code reads
// `permissionSelect.value` — keep a shim so the existing call site doesn't
// have to change.
const PERMISSION_ICONS = { ask: "🔒", plan: "📋", yolo: "⚡" };
let _permissionValue = "ask";
const permissionSelect = {
    get value() { return _permissionValue; },
    set value(v) { _permissionValue = v; updatePermissionButton(); updatePermissionItems(); }
};
const permissionBtnIcon = document.getElementById("permission-btn-icon");
const permissionBtnLabel = document.getElementById("permission-btn-label");
const permissionPopup = document.getElementById("permission-popup");
const permissionControl = document.getElementById("permission-control");

function togglePermissionPopup() {
    permissionPopup.hidden = !permissionPopup.hidden;
}

function setPermission(value) {
    _permissionValue = value;
    updatePermissionButton();
    updatePermissionItems();
    permissionPopup.hidden = true;
}

function updatePermissionButton() {
    permissionBtnIcon.textContent = PERMISSION_ICONS[_permissionValue] || "🔒";
    permissionBtnLabel.textContent = _permissionValue;
}

function updatePermissionItems() {
    permissionPopup.querySelectorAll(".permission-item").forEach(el => {
        el.classList.toggle("active", el.dataset.value === _permissionValue);
    });
}

updatePermissionButton();
updatePermissionItems();

document.addEventListener("click", (e) => {
    if (permissionPopup.hidden) return;
    if (!permissionControl.contains(e.target)) permissionPopup.hidden = true;
}, true);
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

// ── Pinned dock ───────────────────────────────────────────────
// Active interactive cards (a pending AskUserQuestion, the context-usage
// panel) live between the chat and the composer so they can't scroll away
// with the transcript (validation 2026-07-18 — the sticky-bottom approach
// stops working once new content streams in below the card). A hidden
// placeholder marks where the card re-enters the transcript once resolved.
const pinnedDock = document.getElementById("pinned-dock");

function pinCard(card, flowParent, scope) {
    const ph = document.createElement("span");
    ph.className = "pin-placeholder";
    ph.hidden = true;
    flowParent.appendChild(ph);
    card._pinPlaceholder = ph;
    card.dataset.pinScope = scope; // "turn" flushes at stream-done; "manual" waits for ✕
    pinnedDock.appendChild(card);
    pinnedDock.hidden = false;
}

function unpinCard(card) {
    const ph = card._pinPlaceholder;
    card._pinPlaceholder = null;
    delete card.dataset.pinScope;
    // Neutralize the sticky-bottom rule once back in the flow: the "answered"
    // class names it excludes don't match these cards (ask-question-answered,
    // ctx-usage-card), so without this the resolved card floats translucently
    // over the transcript while scrolling (rodada 3 screenshots).
    card.classList.add("pin-resolved");
    if (ph && ph.parentNode) ph.parentNode.replaceChild(card, ph);
    else if (!messages.contains(card)) messages.appendChild(card);
    if (!pinnedDock.firstElementChild) pinnedDock.hidden = true;
    autoScroll();
}

// Turn-scoped cards still docked when the turn ends (cancel, error — the pick
// never came) fall back into the transcript instead of lingering.
function flushPinnedDock() {
    for (const card of [...pinnedDock.children])
        if (card.dataset.pinScope === "turn") unpinCard(card);
}

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
// Counts only user messages (rewind targets the Nth user message, which maps
// 1:1 to user JSONL entries — unlike msgCounter, which mixes in assistant
// bubbles and drifts vs the transcript).
let userMsgCounter = 0;
let currentSessionId = null;
// First user-bubble ordinal still reachable by ⟲ in the CURRENT session's
// JSONL. A mid-chat session change WITHOUT resume starts an empty transcript,
// so bubbles from before that turn have no JSONL entry to rewind to (rodada 3:
// "user ordinal N out of range"). Resumed sessions fork the history and keep
// the base. The ordinal sent to the backend is relative to this base.
let _rewindBaseUserIdx = 0;
// userIndex of the user message that opened the in-flight turn.
let _turnUserIdx = 0;

function decorateMessage(msgEl) {
    const idx = msgCounter++;
    msgEl.dataset.msgIndex = idx;
    const btn = document.createElement("button");
    btn.className = "msg-branch";
    btn.title = "Branch from here";
    btn.textContent = "⎇";
    btn.addEventListener("click", () => branchFromMessage(idx));
    msgEl.appendChild(btn);

    // Rewind affordance only on user messages — it reverts files to the state
    // before that message (native checkpointing). Conversation is kept.
    if (msgEl.classList.contains("user")) {
        const uidx = userMsgCounter++;
        msgEl.dataset.userIndex = uidx;
        const rb = document.createElement("button");
        rb.className = "msg-rewind";
        rb.title = "Rewind files to before this message";
        rb.textContent = "⟲";
        rb.addEventListener("click", () => rewindFromMessage(uidx));
        msgEl.appendChild(rb);
    }
}

function branchFromMessage(idx) {
    if (!currentSessionId) return;
    window.chrome.webview.postMessage({ type: "branch", msgIndex: idx });
}

function rewindFromMessage(idx) {
    if (!currentSessionId) return;
    if (idx < _rewindBaseUserIdx) {
        showToast("This message is from a previous CLI session — rewind can't reach it");
        return;
    }
    // dry run first → preview stats → confirm → apply. The backend indexes into
    // the current session's JSONL, so send the ordinal relative to the base.
    window.chrome.webview.postMessage({ type: "rewind", msgIndex: idx - _rewindBaseUserIdx, dryRun: true });
}

function showRewindConfirm(idx, result) {
    const files = (result && result.filesChanged) || [];
    if (!result || !result.canRewind || files.length === 0) {
        showToast("Nothing to rewind for this message");
        return;
    }
    const ins = result.insertions || 0;
    const del = result.deletions || 0;
    const card = document.createElement("div");
    card.className = "question-card rewind-confirm-card";
    card.innerHTML = `
<div class="question-text">⟲ <strong>Rewind files to before this message?</strong> ${del} line${del === 1 ? "" : "s"} removed and ${ins} added across ${files.length} file${files.length === 1 ? "" : "s"}. The conversation is kept — only files revert.</div>
<ul class="rewind-file-list">${files.map(f => `<li>${escapeHtml(f)}</li>`).join("")}</ul>
<div class="question-buttons">
<button class="q-btn q-yes">Rewind files</button>
<button class="q-btn">Cancel</button>
</div>`;
    const btns = card.querySelectorAll("button");
    btns[0].addEventListener("click", () => {
        card.classList.add("question-answered");
        btns[0].disabled = true; btns[1].disabled = true;
        window.chrome.webview.postMessage({ type: "rewind", msgIndex: idx, dryRun: false });
    });
    btns[1].addEventListener("click", () => card.remove());
    messages.appendChild(card);
    autoScroll();
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
    const all = [...new Set([...builtinCommands, ...customCommands.map(c => c.command), ...discoveredSlashCommands])];
    return query === "/" ? all : all.filter(c => c.startsWith(query));
}

function showAutocomplete(query) {
    const cmds = getMatchingCommands(query);
    if (cmds.length === 0) { hideAutocomplete(); return; }
    acIndex = -1;
    autocompleteEl.innerHTML = cmds.map(c =>
        `<div class="cmd-autocomplete-item" data-cmd="${escapeAttrValue(c)}">${escapeHtml(c)}</div>`
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

// ── "@" file picker (D2) ──────────────────────────────────────
// Typing "@" at the start of a word opens a popup listing workspace files and
// folders; typing filters, ↑/↓ + Enter/Tab (or click) inserts the
// workspace-relative path. Picking a folder keeps the popup open (drill-down).
// The inserted @path is plain text — in stream-json the CLI doesn't expand
// mentions, the model resolves the relative path with its Read tool.
const atAutocompleteEl = document.getElementById("at-autocomplete");
let atIndex = -1;
let atMentionStart = -1;   // index of the triggering '@' in the textarea
let atEntries = null;      // workspace-relative paths ('/'-separated, folders end with '/')
let atEntriesBuiltAt = 0;
let atEntriesLoading = false;
const AT_TTL_MS = 30000;
const AT_MAX_RESULTS = 60;

// An '@' token is an '@' at the start of the text or after whitespace,
// followed by non-whitespace up to the caret.
function findAtToken() {
    const text = textarea.value;
    const caret = textarea.selectionStart;
    for (let i = caret - 1; i >= 0; i--) {
        const c = text[i];
        if (c === "@") {
            if (i === 0 || /\s/.test(text[i - 1])) return { start: i, query: text.slice(i + 1, caret) };
            return null;
        }
        if (/\s/.test(c)) return null;
    }
    return null;
}

function updateAtMention() {
    const tok = findAtToken();
    if (!tok) { hideAtAutocomplete(); return; }
    atMentionStart = tok.start;

    if (!atEntries) {
        showAtIndexing();
        requestWorkspaceFiles();
        return;
    }
    // Stale index: show current results immediately, refresh in background.
    if (Date.now() - atEntriesBuiltAt > AT_TTL_MS && !atEntriesLoading) requestWorkspaceFiles();
    renderAtList(rankAtEntries(tok.query));
}

let _atLoadingTimeout = null;
function requestWorkspaceFiles() {
    if (atEntriesLoading) return;
    atEntriesLoading = true;
    // Safety valve (audit 2026-07-10 #3): if the workspace-files reply never
    // arrives, don't leave the picker stuck on "Indexing…" forever — allow a
    // fresh request on the next keystroke.
    clearTimeout(_atLoadingTimeout);
    _atLoadingTimeout = setTimeout(() => { atEntriesLoading = false; }, 10000);
    try { window.chrome.webview.postMessage({ type: "get-workspace-files" }); }
    catch (e) { atEntriesLoading = false; }
}

// Query may contain "/" (folder drill-down): the part after the last slash
// matches the entry name, the prefix constrains to that subtree. Name
// prefix-matches rank above name/path substring matches.
function rankAtEntries(query) {
    const q = (query || "").replace(/\\/g, "/").toLowerCase();
    const ls = q.lastIndexOf("/");
    const prefix = ls >= 0 ? q.slice(0, ls + 1) : "";
    const namePart = ls >= 0 ? q.slice(ls + 1) : q;

    const startsWith = [], contains = [];
    for (const p of atEntries) {
        const pl = p.toLowerCase();
        if (prefix && !pl.startsWith(prefix)) continue;

        if (!namePart) {
            startsWith.push(p);
        } else {
            const nm = atNameOf(pl);
            if (nm.startsWith(namePart)) startsWith.push(p);
            else if (nm.includes(namePart)) contains.push(p);
            else if (pl.includes(namePart)) contains.push(p);
        }
        if (startsWith.length >= AT_MAX_RESULTS) break;
    }
    return startsWith.concat(contains).slice(0, AT_MAX_RESULTS);
}

function atNameOf(relPath) {
    const t = relPath.replace(/\/+$/, "");
    const s = t.lastIndexOf("/");
    return s >= 0 ? t.slice(s + 1) : t;
}

function renderAtList(items) {
    if (items.length === 0) { hideAtAutocomplete(); return; }
    atIndex = 0;
    atAutocompleteEl.innerHTML = items.map((p, i) => {
        const isDir = p.endsWith("/");
        // title = full path — long entries ellipsize, the tooltip shows the rest
        // (dliedke v58, issue #103). escapeAttrValue, not escapeAttr: the value
        // is read back via dataset, and the JS-string escaper would corrupt
        // paths containing an apostrophe.
        return `<div class="cmd-autocomplete-item at-item${i === 0 ? " ac-selected" : ""}" data-path="${escapeAttrValue(p)}" title="${escapeAttrValue(p)}">${isDir ? "📁" : "📄"} ${escapeHtml(p)}</div>`;
    }).join("");
    atAutocompleteEl.querySelectorAll(".at-item").forEach(el =>
        el.addEventListener("mousedown", e => { e.preventDefault(); commitAtSelection(el.dataset.path); })
    );
    atAutocompleteEl.classList.add("open");
}

function showAtIndexing() {
    atIndex = -1;
    atAutocompleteEl.innerHTML = `<div class="cmd-autocomplete-item at-loading">Indexing workspace…</div>`;
    atAutocompleteEl.classList.add("open");
}

function hideAtAutocomplete() {
    atAutocompleteEl.classList.remove("open");
    atMentionStart = -1;
    atIndex = -1;
}

function moveAtSelection(delta) {
    const items = atAutocompleteEl.querySelectorAll(".at-item");
    if (items.length === 0) return;
    atIndex = Math.max(0, Math.min(items.length - 1, atIndex + delta));
    items.forEach((el, i) => el.classList.toggle("ac-selected", i === atIndex));
    items[atIndex].scrollIntoView({ block: "nearest" });
}

// Replaces the typed "@query" with "@<relative-path>". A file gets a trailing
// space and closes the popup; a folder stays open so the user keeps drilling.
function commitAtSelection(path) {
    if (!path) { hideAtAutocomplete(); return; }
    const caret = textarea.selectionStart;
    if (atMentionStart < 0 || caret < atMentionStart) { hideAtAutocomplete(); return; }

    const isDir = path.endsWith("/");
    const insert = "@" + path + (isDir ? "" : " ");
    const val = textarea.value;
    textarea.value = val.slice(0, atMentionStart) + insert + val.slice(caret);
    const pos = atMentionStart + insert.length;
    textarea.setSelectionRange(pos, pos);
    textarea.focus();
    updateTokenEstimate();

    if (isDir) updateAtMention();
    else hideAtAutocomplete();
}

// Any mousedown inside the popup (scrollbar, padding — not just the items)
// must not steal focus from the textarea: the blur handler below would close
// the picker mid-scroll (audit 2026-07-10 #2; same class as dliedke v58/#103).
atAutocompleteEl.addEventListener("mousedown", e => e.preventDefault());

// Clicking away from the composer closes the picker (mousedown inside the
// popup prevents default, so commits still land before this fires).
textarea.addEventListener("blur", () => setTimeout(hideAtAutocomplete, 150));

textarea.addEventListener("input", () => {
    const val = textarea.value;
    if (val.startsWith("/") && !val.includes(" ")) {
        showAutocomplete(val);
    } else {
        hideAutocomplete();
    }
    updateAtMention();
    updateTokenEstimate();
});

textarea.addEventListener("keydown", (e) => {
    // "@" file picker steals navigation/commit keys while open — before the
    // slash autocomplete and before Enter-sends.
    if (atAutocompleteEl.classList.contains("open")) {
        if (e.key === "ArrowDown") { e.preventDefault(); moveAtSelection(1); return; }
        if (e.key === "ArrowUp") { e.preventDefault(); moveAtSelection(-1); return; }
        if (e.key === "Enter" || e.key === "Tab") {
            // Swallow the key regardless; only insert when entries are ready
            // (while "Indexing…" shows there is nothing to commit yet).
            e.preventDefault();
            const sel = atAutocompleteEl.querySelectorAll(".at-item")[atIndex >= 0 ? atIndex : 0];
            if (sel) commitAtSelection(sel.dataset.path);
            return;
        }
        if (e.key === "Escape") { hideAtAutocomplete(); return; }
    }

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
            updateHistoryHint();
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
        updateHistoryHint();
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
        hideAtAutocomplete();
        sendMessage();
    }

    if (e.ctrlKey && (e.key === "l" || e.key === "L")) {
        e.preventDefault();
        clearChat();
    }
});

// ── /model picker ─────────────────────────────────────────────
const modelList = [
    { id: "claude-sonnet-5",           label: "Sonnet 5" },
    { id: "claude-sonnet-4-6",         label: "Sonnet 4.6" },
    { id: "claude-opus-4-8",           label: "Opus 4.8" },
    { id: "opusplan",                  label: "Opus Plan" },
    { id: "claude-fable-5",            label: "Fable 5" },
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
    // Setting .value programmatically doesn't fire the change event — dispatch
    // it so caption, transcript divider and the titlebar button all update.
    modelSelect.dispatchEvent(new Event("change"));
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
    // A turn is already in flight (possibly still respawning claude — slow and
    // silent): a second send here queues in the agent and the answers tramples
    // the transcript (D7 validation 2026-07-16). Stop it — the user can cancel
    // with ■ first if they really want to abandon the pending turn.
    if (isStreaming) return;
    _userScrolledUp = false;
    const text = textarea.value.trim();
    pushPromptHistory(text);
    historyIndex = -1;
    updateHistoryHint();
    const activeAttachments = [...attachments.values()];

    if (text === "/model") {
        textarea.value = "";
        showModelPicker();
        return;
    }

    if (!text && activeAttachments.length === 0)
        return;

    addMessage("user", text || `(${activeAttachments.map(a => a.displayName).join(", ")})`);
    // Remember which user bubble opened this turn — if the turn spawns a fresh
    // (non-resumed) session, it becomes the new rewind base.
    _turnUserIdx = userMsgCounter - 1;

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
            workingDirectory: getWorkingDirOverride() || null,
            cliPath: getCliPath(),
            // After clearChat / reset-chat, the next send must NOT resume. Otherwise
            // claude.exe gets --continue and reuses the previous session instead of
            // creating a fresh one (turn count keeps going up in the old row).
            autoResume: _suppressNextAutoResume ? false : localStorage.getItem("autoResume") === "true",
            autoSave: autoSaveValues[+autoSaveSlider.value],
            // V7 claude settings (default ON; only deviations affect the spawn).
            coAuthoredBy: localStorage.getItem("coAuthoredBy") !== "false",
            autoCompact: localStorage.getItem("autoCompact") !== "false",
            cleanupPeriodDays: (() => { const v = parseInt(localStorage.getItem("cleanupPeriodDays") || "", 10); return Number.isNaN(v) || v < 1 ? null : v; })(),
            // V6 permission rules — evaluated per-turn by the agent's pipe and
            // written to the generated settings.json for new sessions.
            permissionAllow: getPermRules().allow,
            permissionAsk: getPermRules().ask,
            permissionDeny: getPermRules().deny
        });
        _suppressNextAutoResume = false;
    }
}

function addMessage(role, text) {

    if (welcome) {
        welcome.remove();
    }

    const message = document.createElement("div");
    message.className = `message ${role}`;

    const bubble = document.createElement("div");
    bubble.className = "bubble";
    message.appendChild(bubble);

    // If the message contains a code fence (e.g. snippets inserted via Send
    // Selection, or pasted markdown), render it so the user sees a real code
    // block instead of raw backticks. Plain text falls back to escapeHtml.
    if (text && text.includes("```")) {
        applyMarkdown(bubble, text);
        // Apply button is meaningful for assistant suggestions, not for the
        // user's own message — strip it on the user side.
        if (role === "user") {
            bubble.querySelectorAll(".apply-btn").forEach(b => b.remove());
        }
    } else {
        bubble.innerHTML = escapeHtml(text);
    }

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
        // Remember the VS-detected theme; override (if not "auto") wins via
        // applyThemeOverride, but we keep _lastVsIsDark so switching back to
        // "auto" later snaps to the current VS palette without waiting for a
        // new VS theme event.
        _lastVsIsDark = event.data.isDark;
        applyThemeOverride();
        return;
    }

    if (event.data.type === "account-info") {
        _accountInfo = event.data;
        renderAccountInfo();
        return;
    }

    if (event.data.type === "cwd-info") {
        const prevVsPath = _cwdVsPath;
        renderCwd(event.data.path || "");
        // Workspace identity is now known: migrate the legacy global override
        // (one-shot) and point the settings input at this workspace's entry.
        migrateLegacyWorkingDir();
        syncWorkingDirInput();
        // Workspace changed → the @ picker's file index belongs to the old one,
        // and the "/" autocomplete's discovered commands too.
        if (_cwdVsPath !== prevVsPath) atEntries = null;
        try { window.chrome.webview.postMessage({ type: "get-slash-commands" }); } catch (e) {}
        return;
    }

    if (event.data.type === "trust-required") {
        _workspaceTrusted = false;
        renderCwd();
        openTrustModal(
            event.data.path || "",
            event.data.parent || "",
            event.data.parentIsBlocked === true,
            event.data.riskWarning || null);
        return;
    }

    if (event.data.type === "no-workspace") {
        showNoWorkspaceCard(event.data.path || "");
        return;
    }

    if (event.data.type === "auth-required") {
        showAuthRequiredCard();
        return;
    }

    if (event.data.type === "claude-not-found") {
        showClaudeNotFoundCard(event.data.detail || "");
        return;
    }

    if (event.data.type === "claude-login-started") {
        openSigninOverlay();
        return;
    }

    if (event.data.type === "claude-login-completed") {
        closeSigninOverlay();
        return;
    }

    if (event.data.type === "workspace-trusted") {
        _workspaceTrusted = true;
        renderCwd();
        closeTrustModal();
        return;
    }

    if (event.data.type === "workspace-untrusted") {
        _workspaceTrusted = false;
        renderCwd();
        return;
    }

    if (event.data.type === "trusted-workspaces-list") {
        renderTrustedWorkspacesList(event.data.paths || []);
        return;
    }

    if (event.data.type === "open-trusted-workspaces-modal") {
        openTrustedWorkspacesModal();
        return;
    }

    if (event.data.type === "mcp-trust-required") {
        const overlay = document.getElementById("mcp-trust-modal-overlay");
        if (overlay && overlay.classList.contains("open")) {
            // Modal already open from an earlier scan — merge new servers in
            // (e.g. project scope that resolved later than user scope) instead
            // of nuking the current state.
            mergeMcpTrustModal(event.data.servers || []);
        } else {
            openMcpTrustModal(event.data.servers || []);
        }
        return;
    }

    if (event.data.type === "mcp-trust-completed") {
        closeMcpTrustModal();
        return;
    }

    // (renderCwd also picks up changes from setWorkingDirectory/clearWorkingDirectory
    // below; those handlers call renderCwd() with no argument so the cached vsPath
    // is preserved and only the override layer flips.)

    if (event.data.type === "toast") {
        showToast(event.data.text || "");
        return;
    }

    if (event.data.type === "history") {
        renderHistory(event.data.sessions, event.data.scope, event.data.workspace);
        return;
    }

    if (event.data.type === "session-deleted") {
        const item = document.querySelector(`.history-item[data-session-id="${event.data.sessionId}"]`);
        if (item) item.remove();
        return;
    }

    if (event.data.type === "session-info") {
        const newId = event.data.sessionId;
        // Fresh session mid-chat (no resume/--continue): everything above the
        // current turn is unreachable for ⟲ — anchor the rewind base at the
        // message that opened this session.
        if (currentSessionId && newId !== currentSessionId && !event.data.resumed)
            _rewindBaseUserIdx = _turnUserIdx;
        currentSessionId = newId;
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

    if (event.data.type === "rewind-preview") {
        showRewindConfirm(event.data.msgIndex, event.data.result || {});
        return;
    }

    if (event.data.type === "rewind-done") {
        showToast("Files reverted ✓");
        return;
    }

    if (event.data.type === "rewind-error") {
        showToast(event.data.message || "Rewind failed");
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
        // Turn over: clear any stale pending marker (cancel/dismiss paths skip
        // the modal close hooks); flag "done" only when the window is hidden.
        setCaptionAttention(_toolWindowVisible ? null : "done");
        refreshStatusLine();
        if (isUsageCapture) isUsageCapture = false;
        removeLoading();
        removeLiveTimer();
        if (currentStreamBubble) {
            finalizeBubbleStream(currentStreamBubble);
            currentStreamBubble = null;
        }
        flushPinnedDock();
        renderPresence("", ""); // safety: no waiting state can outlive the turn
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

    if (event.data.type === "system-info") {
        appendSystemInfo(event.data.text || "");
        return;
    }

    if (event.data.type === "permission_request") {
        openPermissionModal(event.data.tool || "", event.data.input || "", event.data.id || "", event.data.cwd || "");
        return;
    }

    if (event.data.type === "visibility-changed") {
        _toolWindowVisible = !!event.data.visible;
        if (_toolWindowVisible && _captionAttention === "done") setCaptionAttention(null);
        return;
    }

    if (event.data.type === "context-usage") {
        renderContextUsageCard(event.data.usage || null, event.data.error || null);
        return;
    }

    if (event.data.type === "mcp-status") {
        renderMcpStatusCard(event.data.servers || null, event.data.error || null);
        return;
    }

    if (event.data.type === "side-question-answer") {
        renderSideQuestionAnswer(event.data.answer || null, event.data.error || null);
        return;
    }

    if (event.data.type === "build-errors") {
        onBuildErrors(event.data);
        return;
    }

    if (event.data.type === "mcp-reconnect-done") {
        // Refresh the open card so the row reflects the post-reconnect state.
        if (event.data.error && _pendingMcpCard) {
            const btn = _pendingMcpCard.querySelector(`.mcp-status-row[data-server="${CSS.escape(event.data.server || "")}"] .mcp-reconnect-btn`);
            if (btn) { btn.disabled = false; btn.textContent = "Reconnect"; }
        }
        try { window.chrome.webview.postMessage({ type: "get-mcp-status" }); } catch (e) {}
        return;
    }

    if (event.data.type === "slash-commands") {
        renderSlashCommands(event.data.project || [], event.data.user || [],
            event.data.projectSkills || [], event.data.userSkills || []);
        return;
    }

    if (event.data.type === "presence-status") {
        renderPresence(event.data.status || "", event.data.waitingFor || "");
        return;
    }

    if (event.data.type === "workspace-files") {
        atEntries = event.data.files || [];
        atEntriesBuiltAt = Date.now();
        atEntriesLoading = false;
        clearTimeout(_atLoadingTimeout);
        // If the picker is waiting ("Indexing workspace…") or open on a stale
        // list, re-render for the token currently under the caret.
        if (atMentionStart >= 0) updateAtMention();
        return;
    }

    if (event.data.type === "status-line") {
        const bar = document.getElementById("statusbar");
        const full = (event.data.text || "").trim();
        if (!full || !(localStorage.getItem("statusLineCommand") || "").trim()) {
            bar.style.display = "none";
            return;
        }
        document.getElementById("statusbar-text").textContent = full.split("\n")[0];
        bar.title = full;
        bar.style.display = "";
        return;
    }

    if (event.data.type === "reset-chat") {
        // Triggered by solution open/close from C# side. Same UX as the user
        // clicking the ✎ button, minus posting "clear" back (backend already reset).
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
        userMsgCounter = 0;
        currentSessionId = null;
        _rewindBaseUserIdx = 0;
        _turnUserIdx = 0;
        updateUsageSessionValues();
        _suppressNextAutoResume = true;
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
    setCaptionAttention("pending");
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
    if (tool === "ExitPlanMode") {
        // Plan approval gate: show the plan markdown itself, not escaped JSON.
        document.getElementById("perm-modal-tool").textContent = "Approve plan? (ExitPlanMode)";
        try { formatted = JSON.parse(input).plan || input; } catch (_) { /* leave as-is */ }
    } else if (tool === "Skill") {
        // Skill gate: title the modal with the skill being invoked (official
        // extension pattern: "Use skill /name?").
        try {
            const parsed = JSON.parse(input);
            const s = String(parsed.skill || "").replace(/^\//, "");
            if (s) document.getElementById("perm-modal-tool").textContent = `Use skill /${s}? (Skill)`;
            formatted = JSON.stringify(parsed, null, 2);
        } catch (_) { /* leave as-is */ }
    } else if (input) {
        try { formatted = JSON.stringify(JSON.parse(input), null, 2); }
        catch (_) { /* leave as-is */ }
    }
    pre.textContent = formatted || "(no input)";

    document.getElementById("perm-modal-overlay").classList.add("open");
    renderPresence("waiting", "permission prompt");
}

function closePermissionModal() {
    if (_captionAttention === "pending") setCaptionAttention(null);
    renderPresence("", "");
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
    const wasPlanApproval = pendingPermissionToolName === "ExitPlanMode";
    window.chrome.webview.postMessage({
        type: "permission-response",
        toolUseId: pendingPermissionToolId,
        allow: true,
        reason: null
    });
    closePermissionModal();

    // Approving a plan makes the CLI leave plan mode (it does NOT return to
    // planning on its own) — mirror that on the composer pill, or the next
    // send claims perm=plan while the session is really in default mode
    // (bloco 20 desync, validation 2026-07-18). Deny keeps everything in plan.
    if (wasPlanApproval && _permissionValue === "plan") {
        setPermission("ask");
        const div = document.createElement("div");
        div.className = "model-divider";
        div.innerHTML = `<span class="model-divider-label"></span>`;
        div.querySelector(".model-divider-label").textContent = "📋 Plan approved — permission mode back to ask";
        messages.appendChild(div);
        autoScroll();
    }
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
    // Plan rejection needs a message claude can act on, not "dismissed".
    if (pendingPermissionToolName === "ExitPlanMode")
        reason = "User rejected the plan — keep planning.";
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

const cwdbarToggle = document.getElementById("cwdbar-toggle");
const _cwdbarVisible = localStorage.getItem("showCwdbar") !== "false";
cwdbarToggle.checked = _cwdbarVisible;
applyCwdbarVisibility(_cwdbarVisible);

function setShowCwdbar(checked) {
    localStorage.setItem("showCwdbar", checked);
    applyCwdbarVisibility(checked);
}

function applyCwdbarVisibility(visible) {
    const bar = document.getElementById("cwdbar");
    if (bar) bar.style.display = visible ? "" : "none";
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

// ── V17 status line ────────────────────────────────────────────
// User-configured command whose output shows in a slim bar under the header.
// Runs in the working directory; refreshed on boot, setting change, and after
// each turn (the turn may have changed git state).
const statusLineInput = document.getElementById("status-line-input");
if (statusLineInput) statusLineInput.value = localStorage.getItem("statusLineCommand") || "";

let _statusLineDebounce = null;
function setStatusLineCommand(value) {
    localStorage.setItem("statusLineCommand", value || "");
    clearTimeout(_statusLineDebounce);
    _statusLineDebounce = setTimeout(refreshStatusLine, 600);
}

function clearStatusLineCommand() {
    statusLineInput.value = "";
    setStatusLineCommand("");
}

function refreshStatusLine() {
    const command = (localStorage.getItem("statusLineCommand") || "").trim();
    if (!command) {
        document.getElementById("statusbar").style.display = "none";
        return;
    }
    try { window.chrome.webview.postMessage({ type: "run-status-line", text: command }); } catch (e) {}
}

window.addEventListener("DOMContentLoaded", refreshStatusLine);

// ── V6 permission rules ────────────────────────────────────────
// {allow:[], ask:[], deny:[]} of claude-style rule strings, persisted in
// localStorage and sent with every chat payload.
function getPermRules() {
    try {
        const r = JSON.parse(localStorage.getItem("permissionRules") || "{}");
        return { allow: r.allow || [], ask: r.ask || [], deny: r.deny || [] };
    } catch (e) { return { allow: [], ask: [], deny: [] }; }
}

function savePermRules(rules) {
    localStorage.setItem("permissionRules", JSON.stringify(rules));
}

function openPermRulesModal() {
    document.getElementById("settings-menu")?.classList.remove("open");
    renderPermRules();
    document.getElementById("perm-rules-overlay").classList.add("open");
}

function closePermRulesModal() {
    document.getElementById("perm-rules-overlay").classList.remove("open");
}

function renderPermRules() {
    const rules = getPermRules();
    for (const bucket of ["allow", "ask", "deny"]) {
        const list = document.getElementById(`perm-rules-${bucket}`);
        const items = rules[bucket];
        list.innerHTML = items.length === 0
            ? `<div class="perm-rules-empty">No rules.</div>`
            : items.map((r, i) =>
                `<div class="perm-rule-row"><span class="perm-rule-text">${escapeHtml(r)}</span>` +
                `<button type="button" class="perm-rule-remove" title="Remove" onclick="removePermRule('${bucket}',${i})">✕</button></div>`
            ).join("");
    }
}

function addPermRule(bucket) {
    const input = document.getElementById(`perm-rules-input-${bucket}`);
    const rule = (input.value || "").trim();
    if (!rule) return;
    const rules = getPermRules();
    if (!rules[bucket].includes(rule)) rules[bucket].push(rule);
    savePermRules(rules);
    input.value = "";
    renderPermRules();
}

function removePermRule(bucket, idx) {
    const rules = getPermRules();
    rules[bucket].splice(idx, 1);
    savePermRules(rules);
    renderPermRules();
}

// ── D3 notification sounds ─────────────────────────────────────
const soundInputToggle = document.getElementById("sound-input-toggle");
if (soundInputToggle) soundInputToggle.checked = localStorage.getItem("soundOnInput") === "true";
function setSoundOnInput(checked) { localStorage.setItem("soundOnInput", checked); }

const soundDoneToggle = document.getElementById("sound-done-toggle");
if (soundDoneToggle) soundDoneToggle.checked = localStorage.getItem("soundOnDone") === "true";
function setSoundOnDone(checked) { localStorage.setItem("soundOnDone", checked); }

// ── Build errors → agent (D11) ─────────────────────────────────
// The ⌘ item asks C# for the current Error List; a failing VS build posts the
// same build-errors message with auto:true. All policy lives here: the opt-in
// auto-send setting, a "same error set twice in a row" dedupe (breaks builds
// looping without progress), and never stepping on a turn in flight.
let _lastBuildErrorsSig = null;

const buildErrorsToggle = document.getElementById("build-errors-toggle");
if (buildErrorsToggle) buildErrorsToggle.checked = localStorage.getItem("autoSendBuildErrors") === "true";
function setAutoSendBuildErrors(checked) { localStorage.setItem("autoSendBuildErrors", checked); }

function requestBuildErrors() {
    document.getElementById("cmd-menu").classList.remove("open");
    window.chrome.webview?.postMessage({ type: "get-build-errors" });
}

function onBuildErrors(msg) {
    if (msg.auto) {
        if (!msg.errorCount) { _lastBuildErrorsSig = null; return; } // green build resets dedupe
        if (localStorage.getItem("autoSendBuildErrors") !== "true") return;
        if (msg.prompt === _lastBuildErrorsSig) return;
        if (isStreaming) return;
    } else {
        if (!msg.errorCount) { showToast("No build errors in the Error List"); return; }
        if (isStreaming) { showToast("A turn is in progress — try again when it finishes"); return; }
    }
    _lastBuildErrorsSig = msg.prompt;
    textarea.value = msg.prompt;
    sendMessage();
}

// ── V7 claude settings (apply to new/restarted sessions) ──────
const coAuthoredToggle = document.getElementById("coauthored-toggle");
if (coAuthoredToggle) coAuthoredToggle.checked = localStorage.getItem("coAuthoredBy") !== "false";
function setCoAuthoredBy(checked) { localStorage.setItem("coAuthoredBy", checked); }

const autoCompactToggle = document.getElementById("autocompact-toggle");
if (autoCompactToggle) autoCompactToggle.checked = localStorage.getItem("autoCompact") !== "false";
function setAutoCompact(checked) { localStorage.setItem("autoCompact", checked); }

const cleanupDaysInput = document.getElementById("cleanup-days-input");
if (cleanupDaysInput) cleanupDaysInput.value = localStorage.getItem("cleanupPeriodDays") || "";
function setCleanupDays(value) {
    const v = parseInt(value, 10);
    if (Number.isNaN(v) || v < 1) localStorage.removeItem("cleanupPeriodDays");
    else localStorage.setItem("cleanupPeriodDays", String(v));
}

// Theme override: "auto" follows VS (default), "dark" / "light" force regardless
// of VS theme. Persists across sessions in localStorage.
let _lastVsIsDark = true; // assume dark until C# reports otherwise
const themeOverrideSelect = document.getElementById("theme-override");
themeOverrideSelect.value = localStorage.getItem("themeOverride") || "auto";

function setThemeOverride(value) {
    localStorage.setItem("themeOverride", value);
    applyThemeOverride();
}

function applyThemeOverride() {
    const mode = localStorage.getItem("themeOverride") || "auto";
    let isDark;
    if (mode === "dark") isDark = true;
    else if (mode === "light") isDark = false;
    else isDark = _lastVsIsDark;
    document.documentElement.classList.toggle("light-theme", !isDark);
}

// Apply on load so the choice is visible even before the first VS theme event.
applyThemeOverride();

// Accent color — user picks a HEX via color input. Persists across sessions.
// Empty localStorage = default (CSS cascade: #d88763 dark, #b05c35 light).
// Reset button clears the override and snaps back to the brand default.
const ACCENT_DEFAULT = "#d88763";
const accentCustomInput = document.getElementById("accent-custom");
accentCustomInput.value = localStorage.getItem("accentCustom") || ACCENT_DEFAULT;

function setAccentCustom(value) {
    localStorage.setItem("accentCustom", value);
    applyAccent();
}

function resetAccent() {
    localStorage.removeItem("accentCustom");
    accentCustomInput.value = ACCENT_DEFAULT;
    applyAccent();
}

function applyAccent() {
    const color = localStorage.getItem("accentCustom");
    if (color) document.documentElement.style.setProperty("--accent", color);
    else document.documentElement.style.removeProperty("--accent");
}

applyAccent();

// Working directory (per-solution overrides — see workingDirKey above)
const workingDirInput = document.getElementById("working-dir-input");
workingDirInput.value = getWorkingDirOverride();

function setWorkingDirectory(value) {
    setWorkingDirOverride(value);
    renderCwd();
    requestCwdRefresh();
}

function clearWorkingDirectory() {
    workingDirInput.value = "";
    setWorkingDirOverride("");
    renderCwd();
    requestCwdRefresh();
    workingDirInput.focus();
}

function syncWorkingDirInput() {
    // Don't clobber the field mid-typing; cwd-info can arrive at any time.
    if (workingDirInput && document.activeElement !== workingDirInput)
        workingDirInput.value = getWorkingDirOverride();
}

// Claude CLI path (D7): explicit claude.exe for installs not on PATH.
const cliPathInput = document.getElementById("cli-path-input");
cliPathInput.value = localStorage.getItem("claudeCliPath") || "";

function getCliPath() {
    return (localStorage.getItem("claudeCliPath") || "").trim() || null;
}

function setCliPath(value) {
    localStorage.setItem("claudeCliPath", value.trim());
}

function clearCliPath() {
    cliPathInput.value = "";
    localStorage.setItem("claudeCliPath", "");
    cliPathInput.focus();
}

function requestCwdRefresh() {
    try { window.chrome.webview.postMessage({ type: "refresh-cwd" }); } catch (e) {}
}

// Display name (override shown in titlebar)
const displayNameInput = document.getElementById("display-name-input");
displayNameInput.value = localStorage.getItem("displayName") || "";

const accountInfoSourceSelect = document.getElementById("account-info-source");
accountInfoSourceSelect.value = localStorage.getItem("accountInfoSource") || "auto";

function setDisplayName(value) {
    localStorage.setItem("displayName", value.trim());
    renderAccountInfo();
}

function clearDisplayName() {
    displayNameInput.value = "";
    localStorage.setItem("displayName", "");
    renderAccountInfo();
    displayNameInput.focus();
}

function setAccountInfoSource(value) {
    localStorage.setItem("accountInfoSource", value);
    renderAccountInfo();
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

// Reset to defaults (D10): clears every settings key and reloads the webview.
// Non-setting data (prompt history, custom commands, sessions) is kept.
const SETTINGS_KEYS = [
    // Appearance
    "themeOverride", "accentCustom",
    // Chat
    "sendWithEnter", "autoResume", "autoSaveLevel", "soundOnInput", "soundOnDone",
    // Display / layout
    "showTokens", "showTokenEstimate", "timingMode", "timeUnit", "compactLayout",
    "composerFontSize", "composerTextareaHeight",
    // Claude Code
    "coAuthoredBy", "autoCompact", "cleanupPeriodDays", "permissionRules", "claudeCliPath",
    // Workspace
    "workingDirOverrides", "workingDirectory", "showCwdbar", "statusLineCommand",
    "displayName", "accountInfoSource",
    // Cost limits (localStorage cache; disk copy cleared via set-cost-limits)
    "costSessionLimit", "costDailyLimit", "costBlock",
    // Composer selectors (incl. legacy effort key)
    "effortLevel", "effortValue", "chatModel"
];

function resetSettingsToDefaults() {
    if (!confirm("Reset all settings to defaults? This includes permission rules, cost limits and per-solution working directories. Prompt history and sessions are kept; the chat view will reload.")) return;
    for (const k of SETTINGS_KEYS) localStorage.removeItem(k);
    // Cost limits also live on disk (cost_limits.json) — clear them there too,
    // or the reload would just sync the old values straight back.
    try {
        window.chrome.webview.postMessage({ type: "set-cost-limits", sessionLimit: null, dailyLimit: null, block: false });
    } catch (e) {}
    // Small delay so the host receives the message before navigation.
    setTimeout(() => location.reload(), 150);
}

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

// Ctrl+Scroll to resize chat font (bubbles, code blocks, composer — all driven
// by the --chat-font-size CSS var). Composer keeps its inline style so the
// textarea matches even when CSS var inheritance gets weird. Same localStorage
// key as before for backward compat with users who already set a custom size.
let composerFontSize = parseFloat(localStorage.getItem("composerFontSize") || "13");

function applyChatFontSize(px) {
    document.documentElement.style.setProperty("--chat-font-size", px + "px");
    textarea.style.fontSize = px + "px";
}
applyChatFontSize(composerFontSize);

document.addEventListener("wheel", e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    composerFontSize = Math.max(8, Math.min(24, composerFontSize + (e.deltaY < 0 ? 1 : -1)));
    applyChatFontSize(composerFontSize);
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

// CLI informational notices ("Unknown command: /x") — muted line in the
// transcript so the turn doesn't read as a silent 0-token no-op.
function appendSystemInfo(text) {
    if (!text) return;
    if (welcome) { welcome.remove(); welcome = null; }
    const el = document.createElement("div");
    el.className = "system-info-line";
    el.textContent = `ⓘ ${text}`;
    messages.appendChild(el);
    autoScroll();
}

function applyMarkdown(bubble, raw) {
    bubble.innerHTML = renderMarkdown(raw);
    bubble.querySelectorAll("pre code").forEach(el => {
        if (typeof hljs !== "undefined") {
            try { hljs.highlightElement(el); } catch (_) { /* unknown language */ }
        }
        // Ensure the .hljs class is present even when the language isn't bundled
        // (dart, dockerfile, etc.). Without it the GitHub Dark theme's background
        // rule (pre code.hljs) doesn't apply and the block bleeds into the bubble.
        if (!el.classList.contains("hljs")) el.classList.add("hljs");
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

    // Links. External URLs keep target=_blank; anything else is treated as a
    // workspace file reference (the appended system prompt instructs claude to
    // emit [file.cs:42](path/file.cs#L42)) and becomes a file-link that opens
    // in the VS editor at the referenced line.
    text = text.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (m, label, href) => {
        if (/^(https?:|mailto:)/i.test(href))
            return `<a href="${href}" target="_blank">${label}</a>`;
        const fm = href.match(/^(.*?)(?:#L(\d+)(?:-L?(\d+))?)?$/);
        const path = (fm[1] || href).replace(/"/g, "&quot;");
        const start = fm[2] || "0";
        const end = fm[3] || fm[2] || "0";
        return `<a href="#" class="file-link" data-path="${path}" data-start="${start}" data-end="${end}" onclick="openFileLink(this);return false;">${label}</a>`;
    });

    // Bare URLs (no markdown syntax) — linkify too. The leading [\s(] guard
    // skips URLs already inside href="..." (preceded by a quote) and anchor
    // labels (preceded by >).
    text = text.replace(/(^|[\s(])(https?:\/\/[^\s<>"')\]]+)/g, '$1<a href="$2" target="_blank">$2</a>');

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
    // Skill invocations carry {skill, args} — show "/name args" instead of the
    // generic first-key fallback (which would just print "skill").
    if (name === "Skill" && input.skill) {
        const s = "/" + String(input.skill).replace(/^\//, "") + (input.args ? " " + String(input.args) : "");
        return s.length > 80 ? s.slice(0, 79) + "…" : s;
    }
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
const askUserQuestionIds = new Set(); // toolIds rendered as a question card — suppress their tool_result chip (the card shows the answer)
const todoWriteIds = new Set();       // toolIds rendered in the todo card — suppress their "Todos modified" tool_result chip
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

// HTML *attribute* escaper — quotes included, unlike escapeHtml (the DOM-based
// one below wins by hoisting and does NOT escape quotes) and unlike escapeAttr
// (which is a JS-string escaper for onclick contexts and would corrupt values
// read back via dataset). Audit 2026-07-10 #6: this was a dead duplicate
// declaration of escapeHtml; renamed and repurposed.
function escapeAttrValue(s) {
    return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
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
        const vsBtnHtml = filePath ? `<button class="tool-diff-vs-btn">abrir no VS</button>` : "";
        container.innerHTML =
            noteHtml +
            `<div class="tool-diff-body">${renderDiffHtml(preview)}</div>` +
            `<div class="tool-diff-actions">
                ${truncated ? `<span class="tool-diff-truncated">+${trimmed.length - PREVIEW_MAX_LINES} mais</span>` : ""}
                ${vsBtnHtml}
                <button class="tool-diff-full-btn">ver completo</button>
            </div>`;
        const btn = container.querySelector(".tool-diff-full-btn");
        btn.addEventListener("click", e => {
            e.stopPropagation();
            openDiffModal(filePath, diffLines, data.name, hasBaseline);
        });
        const vsBtn = container.querySelector(".tool-diff-vs-btn");
        if (vsBtn) vsBtn.addEventListener("click", e => {
            e.stopPropagation();
            openVsDiff(filePath, data.name);
        });
    } catch (ex) {
        container.innerHTML = `<div class="tool-diff-error">${escapeHtml(String(ex))}</div>`;
    }
    autoScroll();
}

// Posts a request to C# to open the native VS side-by-side diff tab (git HEAD
// baseline vs current file). Read-only review.
function openVsDiff(filePath, toolName) {
    if (!filePath) return;
    try {
        window.chrome.webview.postMessage({ type: "open-vs-diff", path: filePath, toolName: toolName || "" });
    } catch (e) { console.warn("open-vs-diff post failed:", e); }
}

// File/tool of the per-tool diff modal currently open, so its "abrir no VS"
// header button knows what to compare.
let toolDiffModalFile = "";
let toolDiffModalTool = "";

function openDiffModal(filePath, diffLines, toolName, hasBaseline) {
    const overlay = document.getElementById("tool-diff-modal-overlay");
    document.getElementById("diff-modal-path").textContent = filePath || "(no path)";
    const subtitle = document.getElementById("diff-modal-subtitle");
    const added = diffLines.filter(l => l.type === "+").length;
    const removed = diffLines.filter(l => l.type === "-").length;
    let sub = `${toolName} • +${added} -${removed}`;
    if (toolName === "Write" && hasBaseline === false) sub += " • new file";
    subtitle.textContent = sub;
    document.getElementById("diff-modal-body").innerHTML = renderDiffHtml(diffLines);
    toolDiffModalFile = filePath || "";
    toolDiffModalTool = toolName || "";
    const vsBtn = document.getElementById("tool-diff-modal-vs");
    if (vsBtn) vsBtn.style.display = filePath ? "" : "none";
    overlay.classList.add("open");
}

function closeToolDiffModal() {
    document.getElementById("tool-diff-modal-overlay").classList.remove("open");
}

function closeDiffModal() {
    document.getElementById("diff-modal-overlay").classList.remove("open");
}

document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    const tool = document.getElementById("tool-diff-modal-overlay");
    const git = document.getElementById("diff-modal-overlay");
    const openOne = (tool && tool.classList.contains("open")) ? tool
                  : (git && git.classList.contains("open")) ? git : null;
    if (!openOne) return;
    e.stopPropagation();
    openOne.classList.remove("open");
}, true);

function appendToolEvent(kind, name, inputJson, text, id) {
    removeLoading();
    const bubble = ensureStreamBubble();

    // AskUserQuestion is a meta tool whose entire purpose is interactive UI.
    // Render a question card with selectable options + write-in. The user's pick
    // is sent back as a control_response (the agent runs claude with
    // --permission-prompt-tool stdio), which becomes the tool's real tool_result.
    if (kind === "tool_use" && name === "AskUserQuestion" && id && inputJson) {
        // Dedupe: JSONL watcher may re-emit the same tool_use after stream-json.
        // While pending the card lives in the dock, not the bubble — check both.
        if (bubble.querySelector(`.ask-question-card[data-tool-id="${CSS.escape(id)}"]`) ||
            pinnedDock.querySelector(`.ask-question-card[data-tool-id="${CSS.escape(id)}"]`)) {
            autoScroll();
            return;
        }
        try {
            renderAskUserQuestionCard(bubble, id, inputJson);
            askUserQuestionIds.add(id);  // suppress the real tool_result chip (card shows the answer)
            // claude is now blocked waiting for the answer — freeze the timer.
            pauseLiveTimer();
            autoScroll();
            return;
        } catch (err) {
            console.warn("AskUserQuestion render failed, falling back to chip:", err);
            // fall through to generic tool_chip path below
        }
    }

    // The AskUserQuestion answer comes back as a real tool_result ("User has
    // answered your questions: ..."). The card already shows the choice, so skip
    // rendering a redundant chip (and avoid the generic branch mislabeling the
    // "last chip" when it can't find one with this id).
    if ((kind === "tool_error" || kind === "tool_result") && id && askUserQuestionIds.has(id)) {
        return;
    }

    // TodoWrite (V21): render the task list as a live card that updates in
    // place as claude progresses through the plan, instead of a generic chip.
    if (kind === "tool_use" && name === "TodoWrite" && inputJson) {
        try {
            renderTodoCard(bubble, inputJson);
            if (id) todoWriteIds.add(id);
            autoScroll();
            return;
        } catch (err) {
            console.warn("TodoWrite render failed, falling back to chip:", err);
            // fall through to the generic chip path below
        }
    }

    // The TodoWrite tool_result is boilerplate ("Todos have been modified
    // successfully") — the card already shows the state, skip the chip.
    if ((kind === "tool_error" || kind === "tool_result") && id && todoWriteIds.has(id)) {
        return;
    }

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

// Renders (or updates in place) the TodoWrite task list. One card per bubble:
// each TodoWrite call carries the FULL current list, so the latest call simply
// replaces the card's contents — the reader sees a live checklist.
function renderTodoCard(bubble, inputJson) {
    const input = JSON.parse(inputJson || "{}");
    const todos = Array.isArray(input.todos) ? input.todos : [];
    if (todos.length === 0) throw new Error("no todos in payload");

    let card = bubble.querySelector(".todo-card");
    if (!card) {
        finalizeActiveSeg(bubble);
        card = document.createElement("div");
        card.className = "todo-card";
        bubble.appendChild(card);
    }

    const done = todos.filter(t => t.status === "completed").length;
    const rows = todos.map(t => {
        if (t.status === "completed")
            return `<div class="todo-item todo-completed">✓ ${escapeHtml(t.content || "")}</div>`;
        if (t.status === "in_progress")
            return `<div class="todo-item todo-inprogress">◔ ${escapeHtml(t.activeForm || t.content || "")}</div>`;
        return `<div class="todo-item todo-pending">○ ${escapeHtml(t.content || "")}</div>`;
    }).join("");

    card.innerHTML = `<div class="todo-card-title">📝 Tasks <span class="todo-progress">${done}/${todos.length}</span></div>` +
        `<div class="todo-list">${rows}</div>`;
}

function renderAskUserQuestionCard(bubble, toolId, inputJson) {
    const input = JSON.parse(inputJson || "{}");
    const questions = Array.isArray(input.questions) ? input.questions : [];
    if (questions.length === 0) throw new Error("no questions in payload");

    finalizeActiveSeg(bubble);

    const card = document.createElement("div");
    card.className = "question-card ask-question-card";
    card.dataset.toolId = toolId;
    setCaptionAttention("pending");

    const answers = {};
    // Multi-select answers are tracked as arrays internally so we can join
    // them in the final payload (claude harness expects a single string per q).
    const multiSelectMap = {};

    const needsConfirm = questions.length > 1 || questions.some(q => q && q.multiSelect);

    questions.forEach((q, qIdx) => {
        const row = document.createElement("div");
        row.className = "ask-question-row";

        if (q.header) {
            const header = document.createElement("div");
            header.className = "ask-question-header";
            header.textContent = q.header;
            row.appendChild(header);
        }

        const text = document.createElement("div");
        text.className = "ask-question-text";
        text.textContent = q.question || "";
        row.appendChild(text);

        const opts = document.createElement("div");
        opts.className = q.multiSelect ? "ask-question-checkboxes" : "ask-question-options";
        const options = Array.isArray(q.options) ? q.options : [];

        options.forEach(opt => {
            if (!opt || !opt.label) return;
            const titleParts = [];
            if (opt.description) titleParts.push(opt.description);
            if (opt.preview) titleParts.push(opt.preview);
            const tooltip = titleParts.join("\n\n");

            if (q.multiSelect) {
                multiSelectMap[q.question] = multiSelectMap[q.question] || new Set();
                const lbl = document.createElement("label");
                lbl.className = "ask-question-check";
                if (tooltip) lbl.title = tooltip;
                const cb = document.createElement("input");
                cb.type = "checkbox";
                cb.value = opt.label;
                cb.onchange = () => {
                    const set = multiSelectMap[q.question];
                    if (cb.checked) set.add(opt.label); else set.delete(opt.label);
                    answers[q.question] = Array.from(set).join(", ");
                    if (set.size === 0) delete answers[q.question];
                    // Multi-select clears any write-in for this row
                    const wi = row.querySelector(".ask-question-other");
                    if (wi) wi.value = "";
                    updateConfirmState();
                };
                lbl.appendChild(cb);
                const span = document.createElement("span");
                span.textContent = opt.label;
                lbl.appendChild(span);
                opts.appendChild(lbl);
            } else {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "q-btn ask-question-btn";
                btn.textContent = opt.label;
                if (tooltip) btn.title = tooltip;
                btn.onclick = () => {
                    opts.querySelectorAll(".ask-question-btn").forEach(b => b.classList.remove("selected"));
                    btn.classList.add("selected");
                    answers[q.question] = opt.label;
                    const wi = row.querySelector(".ask-question-other");
                    if (wi) wi.value = "";
                    maybeAutoSubmit();
                };
                opts.appendChild(btn);
            }
        });

        row.appendChild(opts);

        const otherInput = document.createElement("input");
        otherInput.type = "text";
        otherInput.className = "ask-question-other";
        otherInput.placeholder = "Other — type your own answer…";
        otherInput.oninput = () => {
            const val = otherInput.value.trim();
            if (val) {
                // Write-in overrides any clicked option for this row
                opts.querySelectorAll(".ask-question-btn").forEach(b => b.classList.remove("selected"));
                if (q.multiSelect) {
                    opts.querySelectorAll("input[type=checkbox]").forEach(c => c.checked = false);
                    multiSelectMap[q.question]?.clear();
                }
                answers[q.question] = val;
            } else {
                delete answers[q.question];
            }
            updateConfirmState();
        };
        otherInput.onkeydown = e => {
            if (e.key === "Enter" && otherInput.value.trim()) {
                e.preventDefault();
                maybeAutoSubmit();
            }
        };
        row.appendChild(otherInput);

        card.appendChild(row);
    });

    const footer = document.createElement("div");
    footer.className = "ask-question-footer";

    let confirmBtn = null;
    if (needsConfirm) {
        confirmBtn = document.createElement("button");
        confirmBtn.type = "button";
        confirmBtn.className = "q-btn q-yes ask-question-confirm";
        confirmBtn.textContent = "Submit";
        confirmBtn.disabled = true;
        confirmBtn.onclick = submitAnswers;
        footer.appendChild(confirmBtn);
    }
    card.appendChild(footer);

    // Pending picks dock above the composer so streaming can't scroll them
    // away; submitAnswers moves the card back into the bubble.
    pinCard(card, bubble, "turn");
    renderPresence("waiting", "question");

    function updateConfirmState() {
        if (!confirmBtn) return;
        const allAnswered = questions.every(q => {
            const v = answers[q.question];
            return v && String(v).trim().length > 0;
        });
        confirmBtn.disabled = !allAnswered;
    }

    function maybeAutoSubmit() {
        if (!needsConfirm) submitAnswers();
        else updateConfirmState();
    }

    function submitAnswers() {
        if (card.classList.contains("ask-question-answered")) return;

        // `answers` is already keyed by question text → chosen label (multi-select
        // joined with ", "), which is exactly the shape claude expects in the
        // control_response. Require every question answered before sending.
        const allAnswered = questions.every(q => {
            const v = answers[q.question];
            return v && String(v).trim().length > 0;
        });
        if (!allAnswered) return;

        card.classList.add("ask-question-answered");
        card.querySelectorAll("button, input").forEach(el => el.disabled = true);
        unpinCard(card); // answered — back into the transcript flow
        renderPresence("", "");

        // claude resumes working as soon as it gets the answer — unfreeze the timer.
        resumeLiveTimer();

        // Send the picks back as a control_response (agent forwards to
        // claude.stdin via --permission-prompt-tool stdio). claude unblocks and
        // emits the real tool_result, continuing the SAME turn — no cancel, no
        // new user message, no concurrent-stream juggling.
        try {
            window.chrome.webview.postMessage({
                type: "ask-answer",
                toolUseId: toolId,
                answers: JSON.stringify(answers)
            });
        } catch (e) {
            console.warn("AskUserQuestion answer send failed:", e);
        }
        if (_captionAttention === "pending") setCaptionAttention(null);
    }
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
// While an AskUserQuestion card awaits the user's pick, claude is blocked on our
// control_response — the elapsed time shouldn't count as "working" time. Freeze
// the timer between pause and resume and shift the start forward so the waited
// span is excluded from the displayed elapsed.
let _liveTimerPausedAt = 0;
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
    // Don't restart the ticking interval while paused (awaiting a question answer).
    if (!_liveTimerInterval && !_liveTimerPausedAt) {
        _liveTimerInterval = setInterval(renderLiveTimer, 100);
    }
    messages.appendChild(el);
    autoScroll();
}

// Freeze the live timer while an AskUserQuestion card is pending. No claude
// events arrive during the wait (it's blocked on our reply), so simply stopping
// the interval freezes the display at its last value.
function pauseLiveTimer() {
    if (_liveTimerPausedAt) return;
    _liveTimerPausedAt = Date.now();
    if (_liveTimerInterval) { clearInterval(_liveTimerInterval); _liveTimerInterval = null; }
}

// Resume after the user answers: push the start forward by the paused span so
// the displayed elapsed continues from where it froze.
function resumeLiveTimer() {
    if (!_liveTimerPausedAt) return;
    _liveTimerStart += Date.now() - _liveTimerPausedAt;
    _liveTimerPausedAt = 0;
    if (isStreaming && !_liveTimerInterval && messages.querySelector(".stream-timer")) {
        _liveTimerInterval = setInterval(renderLiveTimer, 100);
    }
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
    _liveTimerPausedAt = 0;
    const el = messages.querySelector(".stream-timer");
    if (el) el.remove();
    _liveTokens = { in: 0, out: 0, cached: 0 };
    _estimatedOutChars = 0;
    _realOutTokens = 0;
}

let _suppressNextAutoResume = false;

function clearChat() {
    pinnedDock.innerHTML = "";
    pinnedDock.hidden = true;
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
    userMsgCounter = 0;
    currentSessionId = null;
    _rewindBaseUserIdx = 0;
    _turnUserIdx = 0;
    updateUsageSessionValues();
    // User explicitly asked for a fresh chat — suppress --continue on the next
    // outbound message even if the "Auto-resume" setting is on.
    _suppressNextAutoResume = true;

    window.chrome.webview.postMessage({ type: "clear" });
}

function renderBranchedMessages(newSessionId, msgs) {
    if (welcome) { welcome.remove(); welcome = null; }
    messages.innerHTML = "";
    msgCounter = 0;
    userMsgCounter = 0;
    currentSessionId = newSessionId;
    // Bubbles are re-rendered 1:1 from this session's JSONL — full range rewindable.
    _rewindBaseUserIdx = 0;
    _turnUserIdx = 0;

    // Replayed chips attach to the preceding assistant bubble, mirroring the
    // live layout (appendToolEvent appends chips inside the stream bubble —
    // rodada 11: as bare siblings they looked "loose" between text bubbles).
    // A turn that opens with tools before any text gets a bare UNdecorated
    // host bubble: it maps to no JSONL text line, so it must not consume a
    // ⎇ ordinal (msgIndex counts DOM text bubbles 1:1 with visible JSONL
    // lines). TodoWrite keeps the live behavior of ONE card per user turn:
    // it pins to its first host bubble and updates in place (last call wins).
    let hostBubble = null;
    let todoBubble = null;

    const ensureHostBubble = () => {
        if (hostBubble) return hostBubble;
        const msg = document.createElement("div");
        msg.className = "message assistant replay-tool-host";
        const bubble = document.createElement("div");
        bubble.className = "bubble";
        msg.appendChild(bubble);
        messages.appendChild(msg);
        hostBubble = bubble;
        return bubble;
    };

    for (const m of msgs) {
        if (m.role === "user") {
            addMessage("user", m.text || "");
            hostBubble = null;
            todoBubble = null;
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
            hostBubble = bubble;
        } else if (m.role === "ask") {
            renderReplayAskCard(m.input, m.answers);
            // The card is a flow sibling — chips appended to the pre-ask
            // bubble would land visually ABOVE it, so start a fresh host.
            hostBubble = null;
        } else if (m.role === "tool") {
            if (m.name === "TodoWrite") {
                if (!todoBubble) todoBubble = ensureHostBubble();
                try { renderTodoCard(todoBubble, m.input); } catch (e) { /* malformed payload — skip */ }
            } else {
                renderReplayToolChip(ensureHostBubble(), m);
            }
        }
    }
    autoScroll();
}

// Re-renders a tool call from a session transcript (History resume / branch)
// as an inert chip inside its assistant host bubble — same look as the live
// chips, including the ↳ result summary and error tint. Edit/Write/MultiEdit keep the inline diff:
// the JSONL input carries the full old/new content, so toggleToolDiff works
// unchanged via toolInputData.
function renderReplayToolChip(container, m) {
    const chip = document.createElement("div");
    chip.className = "tool-chip";
    if (m.error) chip.classList.add("tool-error");

    const dot = document.createElement("span");
    dot.className = "tool-dot";
    dot.textContent = "●";
    chip.appendChild(dot);

    const label = document.createElement("span");
    label.className = "tool-name";
    label.textContent = m.name || "tool";
    chip.appendChild(label);

    const arg = summarizeToolInput(m.name, m.input);
    if (arg) {
        const argEl = document.createElement("span");
        argEl.className = "tool-arg";
        argEl.textContent = `(${arg})`;
        chip.appendChild(argEl);
    }

    if (m.id && DIFF_TOOLS.has(m.name) && m.input) {
        try {
            const parsed = JSON.parse(m.input);
            if (parsed && typeof parsed === "object") {
                toolInputData.set(m.id, { name: m.name, input: parsed });
                chip.dataset.toolId = m.id;
                chip.classList.add("tool-chip-diffable");
                const caret = document.createElement("span");
                caret.className = "tool-diff-caret";
                caret.textContent = "▸";
                chip.appendChild(caret);
                chip.addEventListener("click", e => {
                    if (e.target.closest(".tool-diff-inline")) return;
                    toggleToolDiff(chip);
                });
            }
        } catch { /* leave unclickable */ }
    }

    if (m.result) {
        const summary = document.createElement("div");
        summary.className = "tool-summary";
        const firstLine = String(m.result).split(/\r?\n/)[0];
        summary.textContent = "↳ " + (firstLine.length > 160 ? firstLine.slice(0, 160) + "…" : firstLine);
        chip.appendChild(summary);
    }

    container.appendChild(chip);
}

// Re-renders an AskUserQuestion from a session transcript (History resume /
// branch) as an inert, already-answered card. `answersJson` is the CLI's
// toolUseResult.answers map ({question: label}, multi-select joined ", ") —
// null when the question was dismissed or the session ended unanswered.
// Appended as bare flow content, outside addMessage/decorateMessage, so
// replayed cards never shift branch/rewind ordinals (text bubbles only).
function renderReplayAskCard(inputJson, answersJson) {
    let input;
    try { input = JSON.parse(inputJson || "{}"); } catch (e) { return; }
    const questions = Array.isArray(input.questions) ? input.questions : [];
    if (questions.length === 0) return;
    let answers = {};
    try { answers = JSON.parse(answersJson || "{}") || {}; } catch (e) {}

    const card = document.createElement("div");
    card.className = "question-card ask-question-card ask-question-answered pin-resolved";

    questions.forEach(q => {
        const row = document.createElement("div");
        row.className = "ask-question-row";

        if (q.header) {
            const header = document.createElement("div");
            header.className = "ask-question-header";
            header.textContent = q.header;
            row.appendChild(header);
        }

        const text = document.createElement("div");
        text.className = "ask-question-text";
        text.textContent = q.question || "";
        row.appendChild(text);

        const answer = String(answers[q.question] ?? "");
        // Multi-select answers were joined with ", " on submit; a label can
        // itself contain ", ", so exact match doubles as the fallback.
        const picked = new Set(answer ? answer.split(", ") : []);
        const isPicked = lbl => lbl === answer || picked.has(lbl);

        const opts = document.createElement("div");
        opts.className = q.multiSelect ? "ask-question-checkboxes" : "ask-question-options";
        const options = Array.isArray(q.options) ? q.options : [];
        let anyMatch = false;

        options.forEach(opt => {
            if (!opt || !opt.label) return;
            const hit = isPicked(opt.label);
            anyMatch = anyMatch || hit;
            if (q.multiSelect) {
                const lbl = document.createElement("label");
                lbl.className = "ask-question-check";
                if (opt.description) lbl.title = opt.description;
                const cb = document.createElement("input");
                cb.type = "checkbox";
                cb.checked = hit;
                cb.disabled = true;
                lbl.appendChild(cb);
                const span = document.createElement("span");
                span.textContent = opt.label;
                lbl.appendChild(span);
                opts.appendChild(lbl);
            } else {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "q-btn ask-question-btn" + (hit ? " selected" : "");
                btn.textContent = opt.label;
                if (opt.description) btn.title = opt.description;
                btn.disabled = true;
                opts.appendChild(btn);
            }
        });

        row.appendChild(opts);

        // A write-in answer matches no option label — surface it where the
        // live card's "Other" input sits.
        if (answer && !anyMatch) {
            const other = document.createElement("input");
            other.type = "text";
            other.className = "ask-question-other";
            other.value = answer;
            other.disabled = true;
            row.appendChild(other);
        }

        card.appendChild(row);
    });

    messages.appendChild(card);
}

// Click handler for file-links rendered by renderMarkdown. Path may be
// relative (resolved against the working directory on the C# side) or
// absolute; startLine/endLine are 1-based, 0 = plain open.
function openFileLink(el) {
    window.chrome.webview.postMessage({
        type: "open-file",
        path: el.dataset.path || "",
        startLine: parseInt(el.dataset.start || "0", 10) || 0,
        endLine: parseInt(el.dataset.end || "0", 10) || 0
    });
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

function showNoWorkspaceCard(path) {
    if (welcome) { welcome.remove(); welcome = null; }
    const card = document.createElement("div");
    card.className = "question-card";
    card.innerHTML = `
<div class="question-text">⚠️ <strong>No workspace open.</strong> Open a folder or solution first — Claude won't operate in your home directory (<code>${escapeHtml(path)}</code>).</div>`;
    messages.appendChild(card);
    autoScroll();
}

function showAuthRequiredCard() {
    if (welcome) { welcome.remove(); welcome = null; }
    const card = document.createElement("div");
    card.className = "question-card";
    card.innerHTML = `
<div class="question-text">🔑 <strong>Not signed in.</strong> Claude can't run without an account — sign in to continue.</div>
<div class="question-buttons">
<button class="q-btn q-yes" onclick="startClaudeLogin(this.closest('.question-card'))">Sign in</button>
</div>`;
    messages.appendChild(card);
    autoScroll();
}

function startClaudeLogin(card) {
    try { window.chrome.webview.postMessage({ type: "start-claude-login", cliPath: getCliPath() }); } catch (e) {}
    if (card && card.classList) card.classList.add("question-answered");
}

function showClaudeNotFoundCard(detail) {
    if (welcome) { welcome.remove(); welcome = null; }
    // Avoid stacking duplicate cards when several sends fail in a row.
    const last = messages.lastElementChild;
    if (last && last.classList && last.classList.contains("claude-not-found-card")) return;
    const card = document.createElement("div");
    card.className = "question-card claude-not-found-card";
    card.innerHTML = `
<div class="question-text">🧩 <strong>Claude Code not found.</strong> The agent couldn't find <code>claude.exe</code> on your PATH or any standard install location. Install it, then send your message again.</div>
<div class="claude-install-hint">Documented method: <code>npm install -g @anthropic-ai/claude-code</code> (or choco / the native installer). You may need to restart VS so an updated PATH is picked up. Already installed somewhere custom? Point ⚙ → Claude Code → CLI path at it.</div>
<div class="question-buttons">
<button class="q-btn q-yes" onclick="startClaudeInstall(this.closest('.question-card'))">Install via npm</button>
</div>`;
    messages.appendChild(card);
    autoScroll();
}

function startClaudeInstall(card) {
    try { window.chrome.webview.postMessage({ type: "start-claude-install" }); } catch (e) {}
    if (card && card.classList) card.classList.add("question-answered");
}

function openSigninOverlay() {
    const overlay = document.getElementById("signin-overlay");
    if (overlay) overlay.classList.add("open");
}

function closeSigninOverlay() {
    const overlay = document.getElementById("signin-overlay");
    if (overlay) overlay.classList.remove("open");
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

function renderHistory(sessions, scope, workspace) {
    historySessions = sessions || [];
    historyScope = scope || "all";
    historyWorkspaceName = workspace || null;

    const footer = document.getElementById("history-footer");
    if (footer) {
        if (historyScope === "workspace" && historyWorkspaceName) {
            footer.innerHTML = `📂 ${escapeHtml(historyWorkspaceName)}`;
        } else if (historyScope === "workspace") {
            footer.innerHTML = `📂 workspace`;
        } else {
            footer.innerHTML = `🌐 all projects`;
        }
    }

    renderHistoryList(historySessions, "");
}

function renderHistoryList(sessions, query) {
    const list = document.getElementById("history-list");
    const countEl = document.getElementById("history-count");
    if (countEl) countEl.textContent = (sessions && sessions.length) ? String(sessions.length) : "";
    if (!sessions || sessions.length === 0) {
        if (query) {
            list.innerHTML = `<div class="cmd-item" style="color:#555;font-family:inherit">No matches for "${escapeHtml(query)}"</div>`;
        } else if (historyScope === "workspace") {
            list.innerHTML = `<div class="cmd-item history-empty">No sessions in this workspace · <a href="#" onclick="event.preventDefault();document.getElementById('history-show-all').checked=true;onShowAllToggle();">see all projects</a></div>`;
        } else {
            list.innerHTML = '<div class="cmd-item" style="color:#555;font-family:inherit">No sessions found</div>';
        }
        return;
    }
    list.innerHTML = sessions.map(s => {
        const tok = s.tokens > 1000 ? `${(s.tokens / 1000).toFixed(1)}k tok` : `${s.tokens} tok`;
        const msgs = (s.messages != null) ? `${s.messages} msg${s.messages === 1 ? "" : "s"} · ` : "";
        // Generated/custom title (V18) leads when present; the raw preview
        // stays reachable via tooltip.
        const label = s.title || s.preview;
        const tooltip = s.title ? ` title="${escapeAttr(s.preview)}"` : "";
        return `<div class="cmd-item history-item" data-session-id="${escapeAttr(s.id)}">
  <div class="history-header">
    <div class="history-preview"${tooltip} onclick="resumeSession('${escapeAttr(s.id)}')">${escapeHtml(label)}</div>
    <button class="history-action" onclick="startRenameSession('${escapeAttr(s.id)}')" title="Rename">✎</button>
    <button class="history-action" onclick="viewSession('${escapeAttr(s.id)}')" title="Open transcript in editor">⤢</button>
    <button class="history-delete" onclick="deleteSession('${escapeAttr(s.id)}')" title="Delete session">×</button>
  </div>
  <div class="history-date">${escapeHtml(s.date)} · ${msgs}${tok}</div>
</div>`;
    }).join("");
}

// D4: opens the past session's transcript as readable markdown in the editor.
function viewSession(sessionId) {
    try { window.chrome.webview.postMessage({ type: "view-session", sessionId }); } catch (e) {}
}

// D4: swaps the row's title for an inline input. Enter saves (empty clears the
// custom title, falling back to the generated one), Esc cancels.
function startRenameSession(sessionId) {
    const item = document.querySelector(`.history-item[data-session-id="${CSS.escape(sessionId)}"]`);
    if (!item) return;
    const previewEl = item.querySelector(".history-preview");
    const entry = historySessions.find(s => s.id === sessionId);
    if (!previewEl || !entry) return;

    const input = document.createElement("input");
    input.type = "text";
    input.className = "history-rename-input";
    input.value = entry.title || "";
    input.placeholder = entry.preview || "";
    previewEl.replaceWith(input);
    input.focus();
    input.select();

    let done = false;
    const finish = save => {
        if (done) return;
        done = true;
        if (save) {
            const title = input.value.trim();
            entry.title = title || null;
            try { window.chrome.webview.postMessage({ type: "rename-session", sessionId, title }); } catch (e) {}
        }
        filterHistory(); // re-render with current search applied
    };
    input.addEventListener("keydown", e => {
        e.stopPropagation();
        if (e.key === "Enter") { e.preventDefault(); finish(true); }
        else if (e.key === "Escape") { e.preventDefault(); finish(false); }
    });
    input.addEventListener("blur", () => finish(false));
    input.addEventListener("click", e => e.stopPropagation());
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

let _diffRawText = "";

function renderDiff(stat, diff) {
    _diffStat = stat || "";
    _diffRawText = diff || "";
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
        // No parseable `diff --git` blocks. Show whatever diff text we got
        // (e.g. "Not a git repository. ... Initialize a repository in VS to
        // enable diffs.") instead of leaving the body empty. Wrap in a div
        // with pre-wrap so backend-supplied newlines (\n\n) actually break.
        if (_diffRawText) {
            const msg = document.createElement("div");
            msg.className = "diff-empty-message";
            msg.textContent = _diffRawText;
            body.appendChild(msg);
        } else if (!_diffStat) {
            body.textContent = "No changes.";
        }
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