<script lang="ts">
  type Variant = 'success' | 'warn' | 'idle' | 'error';
  let { variant = 'idle', label }: { variant?: Variant; label: string } = $props();
  const glyphs: Record<Variant, string> = {
    success: '●',
    warn:    '◐',
    idle:    '○',
    error:   '✗',
  };
</script>

<span class={'pill ' + variant}>
  <span class="dot">{glyphs[variant]}</span>
  <span>{label}</span>
</span>
