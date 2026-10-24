"use strict";

let state = null;
let busy = false;
const byId = id => document.getElementById(id);
const note = byId("note");
const encoder = new TextEncoder();
const statusLabels = {
  empty: "Ready to queue",
  queued: "Queued · not delivered",
  carried: "Carried · not delivered",
  delivered: "Delivered at Research Desk",
  expired: "Expired before delivery"
};

function updateControls() {
  const bytes = encoder.encode(note.value).length;
  byId("byte-count").textContent = `${bytes} / 1024 bytes`;
  const hasPacket = Boolean(state?.packetId);
  note.disabled = busy || hasPacket;
  note.hidden = hasPacket;
  byId("queued-note").hidden = !hasPacket;
  byId("byte-count").hidden = hasPacket;
  byId("queue").disabled = busy || !state || hasPacket || !note.value.trim() || bytes > 1024;
  byId("pickup").disabled = busy || !hasPacket || state.station.pending === 0;
  byId("reopen").disabled = busy || !hasPacket || state.courier.pending === 0;
  byId("deliver").disabled = busy || !hasPacket || state.courier.pending === 0;
  byId("offline").disabled = busy || !hasPacket || state.station.pending === 0;
  byId("advance").disabled = busy || !hasPacket || state.clockAdvanced;
  byId("reset").disabled = busy || !state;
  byId("refresh").disabled = busy;
  byId("deliver").textContent = state?.deliveryCount > 0 ? "Retry delivery" : "Deliver note";
}

function showError(message) {
  byId("error").textContent = message;
  byId("error").hidden = !message;
}

function render(next) {
  state = next;
  byId("status").textContent = statusLabels[state.status] ?? state.status;
  byId("status").dataset.status = state.status;
  byId("station-pending").textContent = state.station.pending;
  byId("courier-pending").textContent = state.courier.pending;
  byId("desk-delivered").textContent = state.desk.delivered;
  byId("courier-proof").textContent = state.courierRejectedAsRecipient
    ? "Checked: SDK rejects Courier as recipient" : "Waiting for a pickup";
  byId("recipient-empty").hidden = state.deliveredMessage !== null;
  byId("recipient-empty").textContent = state.status === "expired"
    ? "The note expired before reaching Research Desk. Reset to try again."
    : "Nothing received yet. Queued does not mean delivered.";
  byId("recipient-message").hidden = state.deliveredMessage === null;
  // Never parse user-provided notes, errors, or event text as HTML.
  byId("recipient-message").textContent = state.deliveredMessage ?? "";
  byId("delivery-count").textContent = `${state.deliveryCount} ${state.deliveryCount === 1 ? "delivery" : "deliveries"}`;
  byId("delivery-detail").textContent = state.clockAdvanced && state.deliveryCount > 0
    ? "Queue records have expired. The already-read note above is retained for this demo run; expiry does not retract it."
    : "Delivery is observed here at Research Desk, not acknowledged back to Station. Sender and courier retain copies until expiry.";
  byId("reopen-count").textContent = state.courierReopenCount;
  byId("duplicate-count").textContent = state.duplicateCount;
  byId("clock").textContent = new Date(state.simulatedUtcNow).toISOString().replace("T", " ").replace(/\.\d{3}Z$/, " UTC");
  byId("expires").textContent = state.expiresAtUtc
    ? new Date(state.expiresAtUtc).toISOString().replace("T", " ").replace(/\.\d{3}Z$/, " UTC") : "After you queue a note";
  byId("packet-id").textContent = state.packetId ?? "No packet yet.";
  byId("events").replaceChildren(...state.events.map(event => {
    const item = document.createElement("li");
    item.textContent = event;
    return item;
  }));
  updateControls();
}

async function refresh() {
  const response = await fetch("/api/state", { cache: "no-store" });
  if (!response.ok) throw new Error("Could not read the lab. Check the local server is still running, then refresh.");
  render(await response.json());
}

async function run(action) {
  if (busy) return;
  busy = true;
  showError("");
  updateControls();
  try {
    if (action === "refresh") {
      await refresh();
      return;
    }
    const response = await fetch("/api/step", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-RelayOS-Lab": "1" },
      body: JSON.stringify({ sessionId: state.sessionId, action, message: action === "queue" ? note.value : null })
    });
    if (!response.ok) {
      let message = "The step could not be completed. Refresh state before retrying.";
      try { message = (await response.json()).error ?? message; } catch { /* Keep the bounded fallback. */ }
      // A different tab may have reset the shared run. Do not retry the mutation.
      await refresh();
      throw new Error(message);
    }
    render(await response.json());
  } catch (error) {
    showError(error instanceof TypeError
      ? "Connection interrupted. The step may have completed. Use Refresh state before retrying; all tabs share one run."
      : error.message);
  } finally {
    busy = false;
    updateControls();
  }
}

note.addEventListener("input", updateControls);
byId("note-form").addEventListener("submit", event => { event.preventDefault(); void run("queue"); });
document.querySelectorAll("[data-action]").forEach(button =>
  button.addEventListener("click", () => void run(button.dataset.action)));
byId("refresh").addEventListener("click", () => void run("refresh"));
updateControls();
void run("refresh");
