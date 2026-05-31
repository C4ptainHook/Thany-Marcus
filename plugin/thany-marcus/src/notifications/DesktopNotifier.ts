export interface DesktopNotifyOpts {
  title: string;
  body: string;
  onClick?: () => void;
}

export interface DesktopNotifier {
  requestPermission(): Promise<boolean>;
  notify(opts: DesktopNotifyOpts): void;
  isEnabled(): boolean;
}

export class ElectronDesktopNotifier implements DesktopNotifier {
  private permission: NotificationPermission;

  constructor() {
    this.permission = typeof Notification !== "undefined" ? Notification.permission : "denied";
  }

  isEnabled(): boolean {
    return this.permission === "granted";
  }

  async requestPermission(): Promise<boolean> {
    if (typeof Notification === "undefined") return false;
    if (this.permission === "granted") return true;
    if (this.permission === "denied") return false;
    try {
      const result = await Notification.requestPermission();
      this.permission = result;
      return result === "granted";
    } catch {
      return false;
    }
  }

  notify({ title, body, onClick }: DesktopNotifyOpts): void {
    if (!this.isEnabled()) return;
    if (typeof Notification === "undefined") return;
    const n = new Notification(title, { body, silent: true });
    if (onClick) {
      n.onclick = () => {
        try { window.focus(); } catch { /* ignore */ }
        onClick();
      };
    }
  }
}

export class NoopDesktopNotifier implements DesktopNotifier {
  isEnabled(): boolean { return false; }
  async requestPermission(): Promise<boolean> { return false; }
  notify(_opts: DesktopNotifyOpts): void { /* no-op */ }
}
