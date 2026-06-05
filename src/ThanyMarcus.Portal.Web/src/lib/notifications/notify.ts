import { toast } from 'svelte-sonner';
import { notify as osNotify, isEnabled } from './browserNotifier';
import { flashTitle } from './titleAlert';
import { flashFavicon } from './faviconBadge';

export type UserNotifyVariant = 'success' | 'error' | 'info';

export type UserNotifyOpts = {
  title: string;
  body: string;
  variant: UserNotifyVariant;
  onClick?: () => void;
  durationMs?: number;
};

const MARK: Record<UserNotifyVariant, string> = { success: '✓', error: '✗', info: '✓' };
const BADGE: Record<UserNotifyVariant, string> = { success: '#34A853', error: '#EA4335', info: '#5b8def' };

function pageUnfocused(): boolean {
  return typeof document !== 'undefined' && !document.hasFocus();
}

export function notifyUser({ title, body, variant, onClick, durationMs }: UserNotifyOpts): void {
  const opts = { description: body, duration: durationMs ?? Number.POSITIVE_INFINITY };
  if (variant === 'success') toast.success(title, opts);
  else if (variant === 'error') toast.error(title, opts);
  else toast.info(title, opts);

  if (pageUnfocused()) {
    flashTitle(`${MARK[variant]} ${title}`);
    flashFavicon(BADGE[variant]);
    if (isEnabled()) osNotify({ title, body, onClick });
  }
}
