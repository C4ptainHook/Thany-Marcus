export interface RecorderResult {
  blob: Blob;
  mimeType: string;
  durationMs: number;
}

export class MicRecorder {
  private mediaRecorder: MediaRecorder | null = null;
  private stream: MediaStream | null = null;
  private chunks: BlobPart[] = [];
  private startedAt = 0;
  private mimeType: string;

  constructor(preferredMimeType = "audio/webm;codecs=opus") {
    this.mimeType = preferredMimeType;
  }

  isRecording(): boolean {
    return this.mediaRecorder?.state === "recording";
  }

  async start(): Promise<void> {
    if (this.isRecording()) return;
    this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const mime = MediaRecorder.isTypeSupported(this.mimeType)
      ? this.mimeType
      : "audio/webm";
    this.mediaRecorder = new MediaRecorder(this.stream, { mimeType: mime });
    this.chunks = [];
    this.mediaRecorder.ondataavailable = (e) => {
      if (e.data.size > 0) this.chunks.push(e.data);
    };
    this.mediaRecorder.start();
    this.startedAt = Date.now();
  }

  stop(): Promise<RecorderResult> {
    return new Promise((resolve, reject) => {
      const rec = this.mediaRecorder;
      if (!rec) {
        reject(new Error("recorder not started"));
        return;
      }
      rec.onstop = () => {
        const blob = new Blob(this.chunks, { type: rec.mimeType });
        const duration = Date.now() - this.startedAt;
        this.cleanup();
        resolve({ blob, mimeType: rec.mimeType, durationMs: duration });
      };
      rec.stop();
    });
  }

  cancel(): void {
    if (this.mediaRecorder && this.mediaRecorder.state !== "inactive") {
      try {
        this.mediaRecorder.stop();
      } catch {
        /* ignore */
      }
    }
    this.cleanup();
  }

  elapsedMs(): number {
    return this.isRecording() ? Date.now() - this.startedAt : 0;
  }

  private cleanup(): void {
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
    this.mediaRecorder = null;
    this.chunks = [];
  }
}
