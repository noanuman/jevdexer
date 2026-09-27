"""Measures CLIP text-to-image search over a folder: where does the expected image rank for a query?

Uses the app's int8 vision encoder and CLIP's text encoder (int8, the one the app would download).
Scores are cosine(query, image) minus cosine("a photo", image): how much more the image fits the
query than a neutral description, which cancels images that are simply "photo-like".

Run: python tools/ultra/clip_query.py "<folder>" "<expected file name>" "query one" "query two" ...
"""
import os
import sys
import urllib.request

import numpy as np
import onnxruntime as ort
from PIL import Image
from tokenizers import Tokenizer

CACHE = os.path.join(os.path.dirname(__file__), ".cache")
REPO = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/"
MEAN = np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)
STD = np.array([0.26862954, 0.26130258, 0.27577711], dtype=np.float32)


def fetch(name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name.replace("/", "_"))
    if not os.path.exists(path):
        urllib.request.urlretrieve(REPO + name, path)
    return path


def preprocess(path):
    img = Image.open(path).convert("RGB")
    w, h = img.size
    s = 224 / min(w, h)
    img = img.resize((max(224, round(w * s)), max(224, round(h * s))), Image.BICUBIC)
    w, h = img.size
    l, t = (w - 224) // 2, (h - 224) // 2
    a = (np.asarray(img.crop((l, t, l + 224, t + 224)), dtype=np.float32) / 255 - MEAN) / STD
    return a.transpose(2, 0, 1)


def main():
    folder, expected, queries = sys.argv[1], sys.argv[2], sys.argv[3:]
    vision = ort.InferenceSession(os.path.expandvars(r"%LOCALAPPDATA%\Jevdexer\models\clip-vision-int8.onnx"))
    text = ort.InferenceSession(fetch("onnx/text_model_quantized.onnx"))
    tok = Tokenizer.from_file(fetch("tokenizer.json"))

    cache = os.path.join(CACHE, "images_" + str(abs(hash(folder))) + ".npz")
    if os.path.exists(cache):
        z = np.load(cache, allow_pickle=True)
        files, images = list(z["files"]), z["images"]
    else:
        files, arrays = [], []
        for f in sorted(os.listdir(folder)):
            if not f.lower().endswith((".png", ".jpg", ".jpeg")):
                continue
            try:
                arrays.append(preprocess(os.path.join(folder, f)))
                files.append(f)
            except Exception:
                pass  # unreadable (damaged, or an online-only file that failed to download)
        vecs = []
        for i in range(0, len(arrays), 32):
            v = vision.run(["image_embeds"], {"pixel_values": np.stack(arrays[i:i + 32])})[0]
            vecs.append(v / np.linalg.norm(v, axis=1, keepdims=True))
        images = np.concatenate(vecs)
        np.savez(cache, files=np.array(files, dtype=object), images=images)

    def embed(q):
        ids = np.array([tok.encode(q).ids], dtype=np.int64)
        names = [i.name for i in text.get_inputs()]
        feed = {"input_ids": ids}
        if "attention_mask" in names:
            feed["attention_mask"] = np.ones_like(ids)
        v = text.run(["text_embeds"], feed)[0][0]
        return v / np.linalg.norm(v)

    neutral = images @ embed("a photo")
    at = files.index(expected)
    for q in queries:
        raw = images @ embed(q)
        score = raw - neutral
        order = np.argsort(-score)
        rank = int(np.where(order == at)[0][0]) + 1
        print(f"{q!r}: expected rank {rank}/{len(files)}, score {score[at]:.3f} (raw {raw[at]:.3f}); "
              f"top: {', '.join(f'{files[j]} {score[j]:.3f}' for j in order[:4])}")


if __name__ == "__main__":
    main()
