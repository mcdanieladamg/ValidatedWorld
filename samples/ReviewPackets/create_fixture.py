"""Create sanitized incremental smoke data through the checkout's public CLI."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def node(eid, text, kind="fact", attributes=None, tags=None):
    return {"id": eid, "text": text, "kind": kind, "tags": tags or [], "attributes": attributes or []}


def edge(eid, source, target, relationship="scope-parent", direction="none", rationale=None):
    return {"id": eid, "source": source, "target": target, "relationship": relationship,
            "reviewDirection": direction, "rationale": rationale, "tags": [], "attributes": []}


def rule(count):
    return node("continent-rule", f"Exactly {count} continent records exist.", "validation-rule", [
        {"name": "rule:version", "value": {"kind": "integer", "text": None, "integer": 1, "boolean": False, "instant": None}},
        {"name": "rule:expression", "value": {"kind": "text", "text": json.dumps({"count": {"set": {"nodes": {"kind": "continent"}}, "compare": "eq", "value": count}}), "integer": 0, "boolean": False, "instant": None}}
    ], ["rule:active"])


def fixture(extra_npcs=0):
    values = [
        (node("world", "Tamriel is a fantasy world.", "scope"), "purpose"),
        (node("setting", "The game is medieval fantasy in a cold northern region."), "world"),
        (node("continent-count", "Tamriel has five continents."), "world"),
        (node("continent:A", "Continent A.", "continent"), "world"),
        (node("continent:B", "Continent B.", "continent"), "world"),
        (node("continent:Morrowind", "Morrowind is mostly green.", "continent"), "world"),
        (node("continent:Skyrim", "Skyrim is frozen in the north.", "continent"), "world"),
        (node("continent:E", "Continent E.", "continent"), "world"),
        (node("npc:John", "John lives in Skyrim.", "character"), "continent:Skyrim"),
        (node("dialogue:John", "John brags that Skyrim is the best of all five continents."), "npc:John"),
        (node("npc:Tod", "Tod lives in Skyrim.", "character"), "continent:Skyrim"),
        (node("river", "The river flows down from the mountains."), "continent:Skyrim"),
        (node("dialogue:Tod", "Tod says the river runs down from the mountains."), "npc:Tod"),
        (node("npc:Darrell", "Darrell travels and lives in Morrowind.", "character"), "continent:Morrowind"),
        (node("dialogue:Darrell", "Darrell brags about visiting all five continents in Tamriel."), "npc:Darrell"),
        (node("dialogue:unlinked", "An unlinked character mentions all five continents; the dependency is deliberately missing."), "continent:E"),
        (rule(5), "world"),
    ]
    values += [(node(f"extra:{i:06d}", f"Character {i} lives beside a quiet lake and works as a smith.", "character"), "continent:A") for i in range(extra_npcs)]
    edges = [edge(n["id"] + ":parent", n["id"], parent) for n, parent in values]
    edges += [edge("count:John", "continent-count", "dialogue:John", "informs", "sourceToTarget", "John's dialogue explicitly relies on continent count."),
              edge("count:Darrell", "continent-count", "dialogue:Darrell", "informs", "sourceToTarget", "Darrell's dialogue explicitly relies on continent count."),
              edge("river:Tod", "river", "dialogue:Tod", "informs", "sourceToTarget", "Tod's dialogue relies on river geography.")]
    return [{"kind": "add", "entityKind": "node", "entityId": n["id"], "node": n, "edge": None} for n, _ in values] + [
        {"kind": "add", "entityKind": "edge", "entityId": e["id"], "node": None, "edge": e} for e in edges]


def create(path, extra_npcs=0):
    # Use the same physical spelling for the selected folder and file arguments.
    # OS temp paths can contain macOS links or Windows short-name aliases.
    path = Path(path).expanduser().resolve()
    root = Path(__file__).resolve().parents[2]
    env = dict(os.environ, PYTHONPATH=str(root / "src"))
    process = subprocess.Popen([sys.executable, "-m", "validated_world", "ndjson", str(path.parent)], env=env,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
    def send(command, payload):
        process.stdin.write(json.dumps({"version": 1, "command": command, "payload": payload}) + "\n"); process.stdin.flush()
        response = json.loads(process.stdout.readline())
        if response["status"] != "ok": raise RuntimeError(response)
        return response["payload"]
    try:
        send("project.init", {"path": str(path), "projectId": "game-world", "title": "Sanitized fantasy game", "purposeNodeId": "purpose", "purposeText": "Make a coherent fantasy video game."})
        session = send("change.begin", {"path": str(path), "projectId": "game-world", "author": "fixture", "intent": "Create known sanitized smoke data"})
        session = send("change.apply", {"reference": session["reference"], "operations": {"operations": fixture(extra_npcs)}})
        cursor=None; affected=[]
        while True:
            page=send("change.affected", {"session": {k:session["reference"][k] for k in ("projectId", "sessionId")}, "limit": 20, **({"cursor":cursor} if cursor else {})})
            affected.extend(page["items"]);cursor=page["page"]["nextCursor"]
            if cursor is None:break
        session=send("change.review", {"reference":session["reference"], "dispositions":[{"nodeId":i["value"]["nodeId"], "kind":"updated" if i["value"]["isDirectChange"] else "reviewedNoChange"} for i in affected if i["kind"]=="affectedNode"], "presentedContextNodeIds":[i["value"]["nodeId"] for i in affected if i["kind"]=="scopeContext"]})
        cursor=None
        while True:
            page=send("change.preview", {"reference":session["reference"], "limit":20, **({"cursor":cursor} if cursor else {})})
            cursor=page["reviewPage"]["nextCursor"]
            if cursor is None:break
        result=send("change.write", {"reference":session["reference"]})
        if result["status"] != "written":raise RuntimeError(result)
        return result["project"]
    finally:
        process.stdin.close();process.wait(timeout=10)
        process.stdout.close()


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    parser.add_argument("--extra-npcs", type=int, default=0)
    args=parser.parse_args()
    if args.extra_npcs < 0:parser.error("extra-npcs must be nonnegative")
    print(json.dumps(create(args.path.absolute(), args.extra_npcs)))
