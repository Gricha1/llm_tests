# -*- coding: utf-8 -*-
import json
import urllib.request

B = "http://127.0.0.1:8765"
req = urllib.request.Request(
    B + "/local_chat",
    data=json.dumps({"username": "debug_user", "message": "#exit"}).encode(),
    headers={"Content-Type": "application/json"},
)
print(urllib.request.urlopen(req).read().decode())
print(urllib.request.urlopen(B + "/status").read().decode())
