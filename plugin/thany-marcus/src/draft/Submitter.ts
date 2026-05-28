import { requestUrl, type TFile, type Vault } from "obsidian";
import type {
  ApiClient,
  IngestFinalizeUploadedAttachment,
  IngestInitAttachmentDto,
  IngestInitRequest,
  IngestInitUpload,
} from "../api";
import { mapWithConcurrency } from "../concurrency";
import type { ThanyMarcusSettings } from "../settings";
import type { DraftState } from "./DraftManager";

export interface SubmissionResult {
  noteId: string;
  status: string;
}

const UPLOAD_CONCURRENCY = 4;

export class Submitter {
  constructor(
    private readonly api: ApiClient,
    private readonly vault: Vault,
    private readonly settings: () => ThanyMarcusSettings,
  ) {}

  async submit(state: DraftState, file: TFile): Promise<SubmissionResult> {
    console.log("[Thany.submit] start", { draftId: state.draftId, attachments: state.attachments.length });
    const body = await this.vault.read(file);
    const clientNoteId = crypto.randomUUID();

    const initAtts: IngestInitAttachmentDto[] = state.attachments.map((a) => ({
      clientAttachmentId: a.clientAttachmentId,
      kind: a.kind,
      mimeType: a.mimeType,
      byteSize: a.byteSize,
      sha256: a.sha256,
      filename: a.filename,
      extra: a.extra,
    }));

    const s = this.settings();
    const apiKey = s.privacyMode === "public" ? s.googleApiKey : undefined;

    if (s.privacyMode === "public" && !apiKey) {
      throw new Error(
        `No API key set for ${s.publicModel}. Add it in plugin settings → Synthesis → API keys.`,
      );
    }

    const initReq: IngestInitRequest = {
      clientNoteId,
      capturedAt: new Date().toISOString(),
      body,
      attachments: initAtts,
      privacyMode: s.privacyMode,
      ...(s.privacyMode === "public" ? { publicModel: s.publicModel, llmApiKey: apiKey } : {}),
      synthesisPreset: s.synthesisPreset,
      ...(s.synthesisPreset === "custom" ? { customPrompt: s.customPrompt } : {}),
    };
    console.log("[Thany.submit] initReq", {
      ...initReq,
      llmApiKey: initReq.llmApiKey ? "<redacted>" : undefined,
    });

    let init;
    try {
      init = await this.api.ingestInit(initReq);
      console.log("[Thany.submit] init OK", init);
    } catch (e) {
      console.error("[Thany.submit] init FAILED", e);
      throw e;
    }

    const plan = init.uploads
      .map((upload) => {
        const local = state.attachments.find(
          (a) => a.clientAttachmentId === upload.clientAttachmentId,
        );
        return local && local.vaultPath && local.sha256 && local.byteSize !== null
          ? { upload, local }
          : null;
      })
      .filter((p): p is { upload: IngestInitUpload; local: typeof state.attachments[number] } => p !== null);

    const uploaded = await mapWithConcurrency(plan, UPLOAD_CONCURRENCY, async ({ upload, local }) => {
      const f = this.vault.getAbstractFileByPath(local.vaultPath!) as TFile | null;
      if (!f) throw new Error(`attachment file missing: ${local.vaultPath}`);
      const bytes = await this.vault.readBinary(f);
      console.log("[Thany.submit] PUT prepare", JSON.stringify({
        url: upload.uploadUrl,
        headers: upload.requiredHeaders,
        headerValueTypes: Object.fromEntries(
          Object.entries(upload.requiredHeaders ?? {}).map(([k, v]) => [k, typeof v]),
        ),
        bytes: bytes.byteLength,
      }, null, 2));
      try {
        await this.put(upload.uploadUrl, upload.requiredHeaders, bytes);
        console.log("[Thany.submit] PUT OK", upload.clientAttachmentId);
      } catch (e) {
        console.error("[Thany.submit] PUT FAILED", upload.clientAttachmentId, e);
        throw e;
      }
      return {
        attachmentId: upload.attachmentId,
        sha256: local.sha256!,
        byteSize: local.byteSize!,
      } satisfies IngestFinalizeUploadedAttachment;
    });

    let fin;
    try {
      fin = await this.api.ingestFinalize(init.noteId, uploaded);
      console.log("[Thany.submit] finalize OK", fin);
    } catch (e) {
      console.error("[Thany.submit] finalize FAILED", e);
      throw e;
    }
    return { noteId: init.noteId, status: fin.status };
  }

  private async put(
    url: string,
    headers: Record<string, string>,
    body: ArrayBuffer,
  ): Promise<void> {
    const forbidden = new Set([
      "host", "connection", "content-length", "cookie", "cookie2",
      "date", "dnt", "expect", "keep-alive", "origin",
      "referer", "te", "trailer", "transfer-encoding", "upgrade",
      "user-agent", "via",
    ]);
    const cleanHeaders: Record<string, string> = {};
    for (const [k, v] of Object.entries(headers ?? {})) {
      if (forbidden.has(k.toLowerCase())) continue;
      if (typeof v === "string" && v.length > 0) cleanHeaders[k] = v;
    }
    console.log("[Thany.put] cleaned headers", JSON.stringify(cleanHeaders, null, 2));
    const res = await requestUrl({
      url,
      method: "PUT",
      headers: cleanHeaders,
      body,
      throw: false,
    });
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`PUT presigned upload → HTTP ${res.status}: ${res.text?.slice(0, 200) ?? ""}`);
    }
  }
}
