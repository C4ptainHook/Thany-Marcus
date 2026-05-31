import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ElectronDesktopNotifier, NoopDesktopNotifier } from "./DesktopNotifier";

type FakeNotification = {
  title: string;
  options?: NotificationOptions;
  onclick: ((this: Notification, ev: Event) => unknown) | null;
};

const fakeInstances: FakeNotification[] = [];

class FakeNotificationCtor {
  static permission: NotificationPermission = "default";
  static requestPermission = vi.fn(async () => FakeNotificationCtor.permission);
  title: string;
  options?: NotificationOptions;
  onclick: ((this: Notification, ev: Event) => unknown) | null = null;

  constructor(title: string, options?: NotificationOptions) {
    this.title = title;
    this.options = options;
    fakeInstances.push(this as unknown as FakeNotification);
  }
}

const originalNotification = (globalThis as { Notification?: unknown }).Notification;

beforeEach(() => {
  fakeInstances.length = 0;
  FakeNotificationCtor.permission = "default";
  FakeNotificationCtor.requestPermission = vi.fn(async () => FakeNotificationCtor.permission);
  (globalThis as unknown as { Notification: typeof FakeNotificationCtor }).Notification = FakeNotificationCtor;
});

afterEach(() => {
  if (originalNotification === undefined) {
    delete (globalThis as { Notification?: unknown }).Notification;
  } else {
    (globalThis as unknown as { Notification: unknown }).Notification = originalNotification;
  }
});

describe("ElectronDesktopNotifier", () => {
  it("notify() is a no-op when permission is default", () => {
    FakeNotificationCtor.permission = "default";
    const n = new ElectronDesktopNotifier();
    n.notify({ title: "x", body: "y" });
    expect(fakeInstances).toHaveLength(0);
  });

  it("notify() is a no-op when permission is denied", () => {
    FakeNotificationCtor.permission = "denied";
    const n = new ElectronDesktopNotifier();
    n.notify({ title: "x", body: "y" });
    expect(fakeInstances).toHaveLength(0);
  });

  it("notify() constructs a Notification with silent:true when granted", () => {
    FakeNotificationCtor.permission = "granted";
    const n = new ElectronDesktopNotifier();
    n.notify({ title: "Note ready", body: "Hello" });
    expect(fakeInstances).toHaveLength(1);
    expect(fakeInstances[0].title).toBe("Note ready");
    expect(fakeInstances[0].options).toEqual({ body: "Hello", silent: true });
  });

  it("notify() wires onClick handler", () => {
    FakeNotificationCtor.permission = "granted";
    const handler = vi.fn();
    const n = new ElectronDesktopNotifier();
    n.notify({ title: "t", body: "b", onClick: handler });
    fakeInstances[0].onclick?.call(fakeInstances[0] as unknown as Notification, new Event("click"));
    expect(handler).toHaveBeenCalledOnce();
  });

  it("requestPermission() short-circuits when already granted", async () => {
    FakeNotificationCtor.permission = "granted";
    const n = new ElectronDesktopNotifier();
    expect(await n.requestPermission()).toBe(true);
    expect(FakeNotificationCtor.requestPermission).not.toHaveBeenCalled();
  });

  it("requestPermission() short-circuits when already denied", async () => {
    FakeNotificationCtor.permission = "denied";
    const n = new ElectronDesktopNotifier();
    expect(await n.requestPermission()).toBe(false);
    expect(FakeNotificationCtor.requestPermission).not.toHaveBeenCalled();
  });

  it("requestPermission() transitions default → granted and enables notify()", async () => {
    FakeNotificationCtor.permission = "default";
    const n = new ElectronDesktopNotifier();
    expect(n.isEnabled()).toBe(false);
    FakeNotificationCtor.requestPermission = vi.fn(async () => "granted" as NotificationPermission);
    expect(await n.requestPermission()).toBe(true);
    expect(n.isEnabled()).toBe(true);
    n.notify({ title: "t", body: "b" });
    expect(fakeInstances).toHaveLength(1);
  });
});

describe("NoopDesktopNotifier", () => {
  it("never fires regardless of OS state", async () => {
    FakeNotificationCtor.permission = "granted";
    const n = new NoopDesktopNotifier();
    expect(await n.requestPermission()).toBe(false);
    expect(n.isEnabled()).toBe(false);
    n.notify({ title: "x", body: "y" });
    expect(fakeInstances).toHaveLength(0);
  });
});
