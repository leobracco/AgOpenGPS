function getPathValue(source, path) {
  return path.split(".").reduce((value, key) => value?.[key], source);
}

function formatNumber(value, decimals = 1) {
  const number = Number(value ?? 0);
  return Number.isInteger(number) ? `${number}` : number.toFixed(decimals);
}

function bindState(state) {
  document.querySelectorAll("[data-bind]").forEach((element) => {
    const path = element.dataset.bind;
    let value = getPathValue(state, path);

    if (path === "guidance.auto") value = state.guidance?.isAutoSteerOn ? "AUTO" : "MANUAL";
    if (typeof value === "number") value = formatNumber(value);

    element.textContent = value ?? "";
  });

  document.querySelectorAll("[data-progress]").forEach((element) => {
    const value = Number(getPathValue(state, element.dataset.progress) ?? 0);
    element.style.width = `${Math.min(100, Math.max(0, value))}%`;
  });

  document.body.classList.toggle("gps-warning", !state.gps?.isPositionInitialized);
}

document.querySelectorAll("[data-command]").forEach((button) => {
  button.addEventListener("click", async () => {
    const type = button.dataset.command;
    button.classList.add("pending");
    const result = await window.agpClient.sendCommand(type);
    button.classList.remove("pending");
    console.log(result);
  });
});

window.agpClient.onState(bindState);
window.agpClient.connect();
