for (const toggle of document.querySelectorAll("[data-password-toggle]")) {
    toggle.addEventListener("click", () => {
        const input = document.getElementById(toggle.dataset.target);
        const visible = input.type === "text";
        input.type = visible ? "password" : "text";
        toggle.dataset.visible = String(!visible);
        toggle.setAttribute("aria-pressed", String(!visible));
        toggle.setAttribute("aria-label", visible ? "Show password" : "Hide password");
    });
}
