type EventMap<T> = Partial<Record<string, (data: T) => void>>;

export function subscribeToEvents<T>(url: string, listeners: EventMap<T>): () => void {
  const es = new EventSource(url);
  for (const [eventName, handler] of Object.entries(listeners)) {
    if (!handler) continue;
    es.addEventListener(eventName, (e) => {
      try {
        handler(JSON.parse((e as MessageEvent).data) as T);
      } catch (err) {
        console.error(`SSE handler ${eventName} failed`, err);
      }
    });
  }
  return () => es.close();
}
