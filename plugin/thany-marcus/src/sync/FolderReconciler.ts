export interface FolderReconcileInput {
  // Cloud-relative folder paths present in the vault now.
  vaultFolders: string[];
  // Folder paths the cloud registry currently holds.
  cloudFolders: string[];
}

export interface FolderReconcilePlan {
  toRegister: string[];
  toUnregister: string[];
}

// No circuit breaker (unlike note reconciliation): an unregister only drops a routing candidate.
export function planFolderReconciliation(input: FolderReconcileInput): FolderReconcilePlan {
  const vault = new Set(input.vaultFolders);
  const cloud = new Set(input.cloudFolders);
  return {
    toRegister: [...vault].filter((f) => !cloud.has(f)),
    toUnregister: [...cloud].filter((f) => !vault.has(f)),
  };
}
