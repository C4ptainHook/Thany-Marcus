export type NotifyOpts = {
  title: string;
  body: string;
  onClick?: () => void;
};

export function isSupported(): boolean {
  return typeof Notification !== 'undefined';
}

export function getPermission(): NotificationPermission {
  return isSupported() ? Notification.permission : 'denied';
}

export function isEnabled(): boolean {
  return getPermission() === 'granted';
}

export async function requestPermission(): Promise<NotificationPermission> {
  if (!isSupported()) return 'denied';
  if (Notification.permission !== 'default') return Notification.permission;
  try {
    return await Notification.requestPermission();
  } catch {
    return 'denied';
  }
}

export function notify({ title, body, onClick }: NotifyOpts): void {
  if (!isEnabled()) return;
  const n = new Notification(title, { body, silent: true });
  if (onClick) {
    n.onclick = () => {
      window.focus();
      onClick();
    };
  }
}
