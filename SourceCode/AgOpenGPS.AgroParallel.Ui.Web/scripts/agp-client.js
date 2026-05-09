class AgpClient {
  constructor() {
    this.ws = null;
    this.listeners = new Set();
    this.commandEndpoint = "/api/command";
  }

  onState(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  publishState(state) {
    for (const listener of this.listeners) listener(state);
  }

  connect() {
    const protocol = location.protocol === "https:" ? "wss" : "ws";
    const url = `${protocol}://${location.host}/ws/live`;

    try {
      this.ws = new WebSocket(url);
      this.ws.onmessage = (event) => {
        try { this.publishState(JSON.parse(event.data)); }
        catch (error) { console.warn("Invalid snapshot", error); }
      };
      this.ws.onerror = () => this.startMockMode();
      this.ws.onclose = () => this.startMockMode();
    } catch {
      this.startMockMode();
    }
  }

  startMockMode() {
    if (this.mockInterval) return;
    let t = 0;
    this.mockInterval = setInterval(() => {
      t += 0.08;
      const state = structuredClone(window.agpMockState);
      state.timestamp = new Date().toISOString();
      state.guidance.crossTrackErrorCm = +(2.5 + Math.sin(t) * 1.1).toFixed(1);
      state.machine.speedKph = +(7.2 + Math.sin(t * 0.6) * 0.3).toFixed(1);
      state.machine.headingDegrees = Math.round(74 + Math.sin(t * 0.4) * 2);
      state.field.coveragePercent = Math.min(100, Math.max(0, Math.round(68 + Math.sin(t * 0.2) * 3)));
      this.publishState(state);
    }, 350);
  }

  async sendCommand(type, payload = {}) {
    const command = {
      commandId: crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`,
      timestamp: new Date().toISOString(),
      type,
      payload
    };

    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(JSON.stringify(command));
      return { accepted: true, message: "Command sent to WebView2 bridge." };
    }

    try {
      const response = await fetch(this.commandEndpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(command)
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return await response.json();
    } catch (error) {
      console.info("Mock command", command, error.message);
      return { accepted: true, message: `Mock command: ${type}` };
    }
  }
}

window.agpClient = new AgpClient();
