(() => {
  const button = document.querySelector("[data-passkey-sign-in]");
  const message = document.querySelector("[data-passkey-message]");
  if (!button || !window.PublicKeyCredential || !navigator.credentials) return;

  button.hidden = false;

  const showError = () => {
    message.textContent = "Passkey sign-in failed. Check your details or try again.";
    message.hidden = false;
  };

  const base64UrlToBuffer = (value) => {
    const padding = "=".repeat((4 - (value.length % 4)) % 4);
    const binary = atob((value + padding).replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(binary, (character) => character.charCodeAt(0)).buffer;
  };

  const bufferToBase64Url = (buffer) => {
    const binary = String.fromCharCode(...new Uint8Array(buffer));
    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  };

  button.addEventListener("click", async () => {
    message.hidden = true;
    button.disabled = true;

    try {
      const optionsResponse = await fetch("/auth/passkeys/login/options", { method: "POST" });
      if (!optionsResponse.ok) throw new Error("options");

      const { token, optionsJson } = await optionsResponse.json();
      const options = JSON.parse(optionsJson);
      const credential = await navigator.credentials.get({
        publicKey: {
          ...options,
          challenge: base64UrlToBuffer(options.challenge),
          allowCredentials: (options.allowCredentials || []).map((item) => ({ ...item, id: base64UrlToBuffer(item.id) })),
        },
      });
      if (!credential) throw new Error("credential");

      const assertion = {
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
          authenticatorData: bufferToBase64Url(credential.response.authenticatorData),
          clientDataJSON: bufferToBase64Url(credential.response.clientDataJSON),
          signature: bufferToBase64Url(credential.response.signature),
          userHandle: credential.response.userHandle ? bufferToBase64Url(credential.response.userHandle) : null,
        },
      };
      const completeResponse = await fetch("/auth/passkeys/login/complete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token, assertion }),
      });
      if (!completeResponse.ok) throw new Error("complete");

      window.location.assign(button.dataset.returnUrl || "/");
    } catch {
      showError();
    } finally {
      button.disabled = false;
    }
  });
})();
