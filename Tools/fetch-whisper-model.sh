#!/bin/sh
# Downloads the on-device speech model used by Cascade's voice input (whisper.unity).
# It is ~148 MB, over GitHub's file limit, so it is not committed.
set -e
cd "$(dirname "$0")/.."
mkdir -p Assets/StreamingAssets/Whisper
curl -L -o Assets/StreamingAssets/Whisper/ggml-base.en.bin \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin
echo "Saved Assets/StreamingAssets/Whisper/ggml-base.en.bin"
