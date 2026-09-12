// VSoftSol Syslog Manager — UI helpers (PHASE_04 build item 11: keyboard map).
// No inline script anywhere (CSP has no 'unsafe-inline'); everything lives here.
(function () {
    "use strict";

    const shortcuts = {
        openHelp: null, // set by Blazor via DotNet when the shell mounts (optional)
    };

    // "/" focuses the global search box; "?" is a hint to open the shortcut list.
    document.addEventListener("keydown", function (e) {
        const tag = (e.target && e.target.tagName) || "";
        const typing = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || e.target.isContentEditable;

        if (e.key === "/" && !typing) {
            const search = document.getElementById("global-search");
            if (search) { e.preventDefault(); search.focus(); }
            return;
        }

        if (e.key === "?" && !typing) {
            e.preventDefault();
            window.location.href = "/help";
            return;
        }

        if (e.key === "Escape") {
            document.querySelectorAll("details[open]").forEach(function (d) { d.open = false; });
        }
    });

    window.vsoftsol = {
        // Minimal focus trap for the modal: move focus to the dialog's heading.
        trapFocus: function (headingId) {
            const heading = document.getElementById(headingId);
            if (heading) {
                const dialog = heading.closest("[role='dialog']");
                const focusable = dialog && dialog.querySelector(
                    "button, [href], input, select, textarea, [tabindex]:not([tabindex='-1'])");
                (focusable || heading).focus();
            }
        },
        shortcuts: shortcuts,
    };

    // PHASE_12: the /help page's searchable shortcut list. Self-initializing (driven by
    // markup, not a Blazor interop call) so it works on the page's plain static HTML.
    const helpFilterInput = document.getElementById("help-shortcut-filter");
    if (helpFilterInput) {
        helpFilterInput.addEventListener("input", function () {
            const q = helpFilterInput.value.trim().toLowerCase();
            document.querySelectorAll("[data-shortcut-row]").forEach(function (row) {
                row.hidden = q.length > 0 && row.textContent.toLowerCase().indexOf(q) === -1;
            });
        });
    }

    // PHASE_12: "Waiting for messages" page auto-advance — polls until the collector has
    // stored its first event, then does a real navigation to the dashboard.
    const waitingEl = document.getElementById("vsoftsol-waiting-poll");
    if (waitingEl) {
        const statusUrl = waitingEl.dataset.statusUrl;
        const redirectUrl = waitingEl.dataset.redirectUrl;
        const timer = setInterval(function () {
            fetch(statusUrl, { credentials: "same-origin" })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (data && data.hasMessage) {
                        clearInterval(timer);
                        window.location.href = redirectUrl;
                    }
                })
                .catch(function () { /* transient network hiccup — try again next tick */ });
        }, 3000);
    }
})();
