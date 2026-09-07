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
})();
