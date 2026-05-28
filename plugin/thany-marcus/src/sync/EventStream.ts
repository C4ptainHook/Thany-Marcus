export type SseEventKind =
  | "note_phase_changed"
  | "attachment_status_changed"
  | "note_succeeded"
  | "note_failed"
  | "hub_materialized";

export interface SseEvent {
  kind: SseEventKind | string;
  data: Record<string, unknown>;
}

export type SseHandler = (e: SseEvent) => void;

const RECONNECT_DELAY_MS = 60_000;

/**
 * EventSource cannot send Authorization headers, so we drive SSE via fetch + a
 * ReadableStream reader and a tiny parser.
 */
export class EventStream {
  private controller: AbortController | null = null;
  private reconnectTimer: number | null = null;
  private stopped = false;

  constructor(
    private readonly url: () => string,
    private readonly token: () => string,
    private readonly handler: SseHandler,
    private readonly onError: (e: Error) => void,
  ) {}

  start(): void {
    this.stopped = false;
    void this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.controller?.abort();
    this.controller = null;
  }

  private scheduleReconnect(): void {
    if (this.stopped) return;
    if (this.reconnectTimer !== null) return;
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null;
      void this.connect();
    }, RECONNECT_DELAY_MS);
  }

  private async connect(): Promise<void> {
    if (this.stopped) return;
    this.controller = new AbortController();
    try {
      const res = await fetch(this.url(), {
        method: "GET",
        headers: {
          Authorization: `Bearer ${this.token()}`,
          Accept: "text/event-stream",
        },
        signal: this.controller.signal,
      });
      if (!res.ok || !res.body) {
        throw new Error(`SSE connect HTTP ${res.status}`);
      }
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buf = "";
      // eslint-disable-next-line no-constant-condition
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        let idx: number;
        while ((idx = buf.indexOf("\n\n")) !== -1) {
          const chunk = buf.slice(0, idx);
          buf = buf.slice(idx + 2);
          const evt = parseChunk(chunk);
          if (evt) this.handler(evt);
        }
      }
      this.scheduleReconnect();
    } catch (e) {
      if ((e as Error).name === "AbortError") return;
      this.onError(e as Error);
      this.scheduleReconnect();
    }
  }
}

function parseChunk(chunk: string): SseEvent | null {
  let kind: string | null = null;
  const dataLines: string[] = [];
  for (const raw of chunk.split("\n")) {
    if (raw.startsWith(":")) continue;
    const colon = raw.indexOf(":");
    if (colon < 0) continue;
    const field = raw.slice(0, colon);
    const value = raw.slice(colon + 1).replace(/^ /, "");
    if (field === "event") kind = value;
    else if (field === "data") dataLines.push(value);
  }
  if (!kind || dataLines.length === 0) return null;
  try {
    return { kind, data: JSON.parse(dataLines.join("\n")) as Record<string, unknown> };
  } catch {
    return { kind, data: {} };
  }
}
