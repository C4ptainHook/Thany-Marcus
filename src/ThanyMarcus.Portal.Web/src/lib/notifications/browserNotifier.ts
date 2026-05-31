export type NotifyOpts = {
  title: string;
  body: string;
  onClick?: () => void;
};

let cached: NotificationPermission =
  typeof Notification !== "undefined" ? Notification.permission : "denied";

export function isSupported(): boolean {
  return typeof Notification !== "undefined";
}

export function isEnabled(): boolean {
  return cached === "granted";
}

export async function requestPermission(): Promise<boolean> {
  if (!isSupported()) return false;
  if (cached === "granted") return true;
  if (cached === "denied") return false;
  try {
    const result = await Notification.requestPermission();
    cached = result;
    return result === "granted";
  } catch {
    return false;
  }
}

export function notify({ title, body, onClick }: NotifyOpts): void {
  if (!isSupported() || !isEnabled()) return;
  const n = new Notification(title, { body, silent: true });
  if (onClick) {
    n.onclick = () => {
      try { window.focus(); } catch { /* ignore */ }
      onClick();
    };
  }
}

export function __resetForTests(p: NotificationPermission): void {
  cached = p;
}
