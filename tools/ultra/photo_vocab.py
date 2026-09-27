"""Builds the photo vocabulary for Ultra Search's local photo tags.

CLIP scores an image against text. Instead of shipping CLIP's text half (and its tokenizer), we embed
a fixed list of concepts once, here, and ship the vectors (src/Jevdexer.Content/Models/photo-vocab.json).
The app then needs only the image encoder: tags = the concepts nearest to a photo.

Must use the same CLIP as the app's image encoder: Xenova/clip-vit-base-patch32 (openai ViT-B/32).

Run: python tools/ultra/photo_vocab.py   (needs onnxruntime, tokenizers, numpy; downloads ~250 MB once)
"""
import base64
import json
import os
import urllib.request

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

REPO = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/"
CACHE = os.path.join(os.path.dirname(__file__), ".cache")
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Jevdexer.Content", "Models", "photo-vocab.json")

# (label shown in summaries, prompt, sensitive). Sensitive tags make a folder local-only by default.
CONCEPTS = [
    # people
    ("woman", "a photo of a woman", False), ("man", "a photo of a man", False), ("child", "a photo of a child", False),
    ("couple", "a photo of a couple", False), ("people", "a photo of a group of people", False),
    ("crowd", "a photo of a crowd", False), ("person smiling", "a photo of a person smiling", False),
    # life events
    ("wedding", "a photo of a wedding", False), ("birthday party", "a photo of a birthday party", False),
    ("graduation", "a photo of a graduation ceremony", False), ("concert", "a photo of a concert", False),
    ("party", "a photo of people at a party", False), ("holiday dinner", "a photo of a family holiday dinner", False),
    ("christmas", "a photo of christmas decorations", False), ("new year fireworks", "a photo of fireworks", False),
    ("baby", "a photo of a baby", False), ("children playing", "a photo of children playing", False),
    ("family photo", "a family photo", False), ("group photo", "a group photo of friends", False),
    ("selfie", "a selfie", False), ("portrait", "a portrait photo of a person", False),
    ("sports game", "a photo of a sports game", False), ("football", "a photo of a football match", False),
    ("basketball", "a photo of basketball", False), ("gym workout", "a photo of a gym workout", False),
    ("hiking", "a photo of people hiking", False), ("skiing", "a photo of skiing", False),
    ("swimming", "a photo of people swimming", False), ("camping", "a photo of camping with a tent", False),
    ("road trip", "a photo from a road trip", False), ("festival", "a photo of a music festival", False),
    ("church", "a photo of a church", False), ("religious ceremony", "a photo of a religious ceremony", False),
    ("funeral", "a photo of a funeral", False), ("school", "a photo of a school classroom", False),
    ("office", "a photo of an office", False), ("conference", "a photo of a conference presentation", False),
    ("formal clothing", "a photo of people in formal clothing", False), ("wedding dress", "a photo of a wedding dress", False),
    ("cake", "a photo of a cake", False),
    # places
    ("beach", "a photo of a beach", False), ("sea", "a photo of the sea", False), ("mountains", "a photo of mountains", False),
    ("snow", "a photo of snow", False), ("forest", "a photo of a forest", False), ("lake", "a photo of a lake", False),
    ("river", "a photo of a river", False), ("city", "a photo of a city street", False), ("night city", "a photo of a city at night", False),
    ("old town", "a photo of an old town", False), ("countryside", "a photo of the countryside", False),
    ("village", "a photo of a village", False), ("garden", "a photo of a garden", False), ("park", "a photo of a park", False),
    ("sunset", "a photo of a sunset", False), ("desert", "a photo of a desert", False), ("island", "a photo of an island", False),
    ("pool", "a photo of a swimming pool", False), ("hotel", "a photo of a hotel room", False), ("airport", "a photo of an airport", False),
    ("airplane", "a photo taken from an airplane window", False), ("train", "a photo of a train", False),
    ("museum", "a photo of a museum", False), ("monument", "a photo of a monument", False), ("castle", "a photo of a castle", False),
    ("restaurant", "a photo of a restaurant", False), ("cafe", "a photo of a cafe", False), ("bar", "a photo of a bar", False),
    ("kitchen", "a photo of a kitchen", False), ("living room", "a photo of a living room", False), ("bedroom", "a photo of a bedroom", False),
    ("house", "a photo of a house", False), ("apartment", "a photo of an apartment interior", False),
    ("construction", "a photo of a construction site", False), ("stadium", "a photo of a stadium", False),
    ("shop", "a photo of a shop", False), ("market", "a photo of a market", False),
    # things
    ("food", "a photo of food", False), ("pizza", "a photo of pizza", False), ("drinks", "a photo of drinks", False),
    ("coffee", "a photo of coffee", False), ("wine", "a photo of wine", False), ("dessert", "a photo of a dessert", False),
    ("car", "a photo of a car", False), ("motorcycle", "a photo of a motorcycle", False), ("bicycle", "a photo of a bicycle", False),
    ("boat", "a photo of a boat", False), ("dog", "a photo of a dog", False), ("cat", "a photo of a cat", False),
    ("horse", "a photo of a horse", False), ("bird", "a photo of a bird", False), ("animals", "a photo of animals", False),
    ("flowers", "a photo of flowers", False), ("plants", "a photo of plants", False), ("trees", "a photo of trees", False),
    ("christmas tree", "a photo of a christmas tree", False), ("gifts", "a photo of gifts", False),
    ("clothes", "a photo of clothes", False), ("shoes", "a photo of shoes", False), ("furniture", "a photo of furniture", False),
    ("computer", "a photo of a computer", False), ("phone", "a photo of a phone", False), ("books", "a photo of books", False),
    ("art", "a photo of a painting", False), ("drawing", "a drawing", False), ("sculpture", "a photo of a sculpture", False),
    ("music instrument", "a photo of a musical instrument", False), ("guitar", "a photo of a guitar", False),
    ("toys", "a photo of toys", False), ("video game", "a screenshot of a video game", False),
    # documents and screens
    ("screenshot", "a screenshot of a computer screen", False), ("phone screenshot", "a screenshot of a phone screen", False),
    ("chat screenshot", "a screenshot of a chat conversation", False), ("website", "a screenshot of a website", False),
    ("code", "a screenshot of source code", False), ("chart", "a chart or graph", False), ("diagram", "a diagram", False),
    ("map", "a map", False), ("slide", "a presentation slide", False), ("spreadsheet", "a screenshot of a spreadsheet", False),
    ("document", "a photo of a printed document", False), ("handwritten notes", "a photo of handwritten notes", False),
    ("whiteboard", "a photo of a whiteboard", False), ("book page", "a photo of a book page", False),
    ("math", "a photo of math equations", False), ("receipt", "a photo of a receipt", False), ("invoice", "a photo of an invoice", False),
    ("ticket", "a photo of a ticket", False), ("menu", "a photo of a restaurant menu", False), ("poster", "a poster", False),
    ("logo", "a logo", False), ("icon", "an app icon", False), ("meme", "a meme", False), ("wallpaper", "a desktop wallpaper", False),
    ("product photo", "a product photo on a white background", False), ("floor plan", "a floor plan", False),
    # sensitive: the folder becomes local-only by default
    ("id card", "a photo of an identity card", True), ("passport", "a photo of a passport", True),
    ("driving licence", "a photo of a driving licence", True), ("bank card", "a photo of a credit card", True),
    ("medical scan", "an x-ray or medical scan", True), ("medical document", "a photo of a medical report", True),
    ("prescription", "a photo of a medical prescription", True), ("contract", "a photo of a signed contract", True),
    ("bank statement", "a photo of a bank statement", True), ("password", "a screenshot of passwords", True),
    ("qr code", "a qr code", True),
]


def fetch(name: str) -> str:
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name.replace("/", "_"))
    if not os.path.exists(path):
        urllib.request.urlretrieve(REPO + name, path)
    return path


def main() -> None:
    tokenizer = Tokenizer.from_file(fetch("tokenizer.json"))
    session = ort.InferenceSession(fetch("onnx/text_model.onnx"))
    names = [i.name for i in session.get_inputs()]
    out_names = [o.name for o in session.get_outputs()]
    assert "text_embeds" in out_names, out_names

    items = []
    for label, prompt, sensitive in CONCEPTS:
        ids = np.array([tokenizer.encode(prompt).ids], dtype=np.int64)
        feed = {"input_ids": ids}
        if "attention_mask" in names:
            feed["attention_mask"] = np.ones_like(ids)
        v = session.run(["text_embeds"], feed)[0][0].astype(np.float32)
        v /= np.linalg.norm(v)
        items.append({"label": label, "sensitive": sensitive, "vector": base64.b64encode(v.tobytes()).decode()})

    with open(OUT, "w", encoding="utf8") as f:
        json.dump({"model": "Xenova/clip-vit-base-patch32", "dimensions": 512, "concepts": items}, f)
    print(f"{len(items)} concepts -> {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()
