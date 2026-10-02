(() => {
  const escapeHtml = (value) => {
    const element = document.createElement("div");
    element.textContent = value ?? "";
    return element.innerHTML;
  };
  const showMessage = (container, text, success = false) => {
    if (!container) return;
    container.innerHTML = text ? `<p class="message ${success ? "message--success" : "message--error"}" role="alert">${escapeHtml(text)}</p>` : "";
  };
  const toBuffer = (value) => {
    const padding = "=".repeat((4 - (value.length % 4)) % 4);
    const binary = atob((value + padding).replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(binary, (character) => character.charCodeAt(0)).buffer;
  };
  const toBase64Url = (buffer) => btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

  const sessionsList = document.querySelector("[data-session-list]");
  if (sessionsList) {
    const loadSessions = async () => {
      const response = await fetch("/auth/sessions");
      if (!response.ok) { sessionsList.innerHTML = '<p class="message--empty">Could not load your sessions.</p>'; return; }
      const sessions = await response.json();
      sessionsList.innerHTML = sessions.map((session) => `<div class="session-row"><div><strong>${escapeHtml(session.ipAddress || "Unknown IP")}</strong>${session.isCurrent ? '<span class="badge--current">This device</span>' : ""}<p class="session-row__meta">${escapeHtml(session.userAgent || "Unknown device")}</p><p class="session-row__meta">Signed in ${new Date(session.createdAt).toLocaleString()}</p></div><button type="button" class="button-secondary" data-revoke-session="${escapeHtml(session.id)}">Sign out</button></div>`).join("") || '<p class="message--empty">No active sessions.</p>';
      sessionsList.querySelectorAll("[data-revoke-session]").forEach((button) => button.addEventListener("click", async () => {
        button.disabled = true;
        if ((await fetch(`/auth/sessions/${button.dataset.revokeSession}`, { method: "DELETE" })).ok) await loadSessions(); else button.disabled = false;
      }));
    };
    void loadSessions();
  }

  const passkeysList = document.querySelector("[data-passkeys-list]");
  const passkeysMessage = document.querySelector("[data-passkeys-message]");
  const registerPasskey = document.querySelector("[data-passkeys-register]");
  if (passkeysList && registerPasskey) {
    const loadPasskeys = async () => {
      const response = await fetch("/auth/passkeys");
      if (!response.ok) { passkeysList.innerHTML = '<p class="message--empty">Could not load your passkeys.</p>'; return; }
      const passkeys = await response.json();
      passkeysList.innerHTML = passkeys.map((passkey) => `<div class="passkey-row"><div><strong>${escapeHtml(passkey.name)}</strong><p class="passkey-row__meta">Added ${new Date(passkey.createdAt).toLocaleDateString()}${passkey.lastUsedAt ? ` · last used ${new Date(passkey.lastUsedAt).toLocaleDateString()}` : ""}</p></div><button type="button" class="button-secondary" data-delete-passkey="${escapeHtml(passkey.id)}">Remove</button></div>`).join("") || '<p class="message--empty">No passkeys registered yet.</p>';
      passkeysList.querySelectorAll("[data-delete-passkey]").forEach((button) => button.addEventListener("click", async () => {
        button.disabled = true;
        if ((await fetch(`/auth/passkeys/${button.dataset.deletePasskey}`, { method: "DELETE" })).ok) await loadPasskeys(); else { button.disabled = false; showMessage(passkeysMessage, "Could not remove that passkey."); }
      }));
    };
    registerPasskey.addEventListener("click", async () => {
      showMessage(passkeysMessage, "");
      if (!window.PublicKeyCredential) { showMessage(passkeysMessage, "This browser does not support passkeys."); return; }
      registerPasskey.disabled = true;
      try {
        const response = await fetch("/auth/passkeys/register/options", { method: "POST" });
        if (!response.ok) throw new Error("options");
        const { token, optionsJson } = await response.json(); const options = JSON.parse(optionsJson);
        const credential = await navigator.credentials.create({ publicKey: { ...options, challenge: toBuffer(options.challenge), user: { ...options.user, id: toBuffer(options.user.id) }, excludeCredentials: (options.excludeCredentials || []).map((item) => ({ ...item, id: toBuffer(item.id) })) } });
        if (!credential) throw new Error("credential");
        const label = document.querySelector("#passkey-label").value.trim() || "Passkey";
        const complete = await fetch("/auth/passkeys/register/complete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token, label, attestation: { id: credential.id, rawId: toBase64Url(credential.rawId), type: credential.type, response: { attestationObject: toBase64Url(credential.response.attestationObject), clientDataJSON: toBase64Url(credential.response.clientDataJSON), transports: credential.response.getTransports ? credential.response.getTransports() : [] } } }) });
        if (!complete.ok) throw new Error("complete");
        document.querySelector("#passkey-label").value = ""; await loadPasskeys();
      } catch { showMessage(passkeysMessage, "That passkey could not be registered."); } finally { registerPasskey.disabled = false; }
    });
    void loadPasskeys();
  }

  const changeEmail = document.querySelector("[data-change-email]");
  if (changeEmail) {
    const emailMessage = document.querySelector("[data-email-message]"); const deleteMessage = document.querySelector("[data-delete-message]");
    changeEmail.addEventListener("click", async () => {
      const newEmail = document.querySelector("#new-email").value.trim(); const password = document.querySelector("#email-password").value;
      const response = await fetch("/auth/email/request-change", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ newEmail, password }) });
      document.querySelector("#email-password").value = "";
      if (response.ok) { document.querySelector("#new-email").value = ""; showMessage(emailMessage, "Check the new address for a confirmation link.", true); } else showMessage(emailMessage, "Could not start the email change. Check your password and try again.");
    });
    document.querySelector("[data-delete-account]").addEventListener("click", async () => {
      if (!window.confirm("This permanently deletes your account. Continue?")) return;
      const password = document.querySelector("#delete-password").value;
      const response = await fetch("/auth/account", { method: "DELETE", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password }) });
      document.querySelector("#delete-password").value = "";
      if (response.ok) window.location.assign("/auth/sign-in"); else showMessage(deleteMessage, "Could not delete your account. Check your password and try again.");
    });
  }
})();
