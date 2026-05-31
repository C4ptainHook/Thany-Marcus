import { writable } from 'svelte/store';

export const forgotPassphraseOpen = writable(false);

export function openForgotPassphrase() {
  forgotPassphraseOpen.set(true);
}

export function closeForgotPassphrase() {
  forgotPassphraseOpen.set(false);
}
