#!/usr/bin/env sh
# Stage the Granite Embedding R2 ONNX + tokenizer for local dev.
# Run once after `git clone` (or when bumping GRANITE_REVISION in CI).
set -eu

REPO="${1:-ibm-granite/granite-embedding-311m-multilingual-r2}"
REV="${2:-main}"
DEST="src/ThanyMarcus.Cloud.Api/models/granite"

if ! command -v huggingface-cli >/dev/null 2>&1; then
    echo "huggingface-cli not found. Install with: pip install 'huggingface_hub[cli]'" >&2
    exit 1
fi

mkdir -p "$DEST"
huggingface-cli download "$REPO" --revision "$REV" \
    --include "onnx/model.onnx" "tokenizer.json" "config.json" \
    --local-dir "$DEST"

echo "Granite model staged at $DEST"
echo "  onnx/model.onnx   $(du -h "$DEST/onnx/model.onnx" 2>/dev/null | cut -f1)"
echo "  tokenizer.json    $(du -h "$DEST/tokenizer.json"  2>/dev/null | cut -f1)"
