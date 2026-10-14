#!/usr/bin/env python3
"""PreToolUse reporter: tells the Obsidian graph which vault files are in use.

The plugin `graph-pulse` polls the activity file and lets the matching node
light up. Node ids in the graph are vault relative paths, so every path is
resolved through realpath first: the auto-memory directory is a symlink and
would otherwise never match a node.

This hook never blocks and never prints. A failure here must not cost a tool
call, so every error path ends in exit 0.
"""

import fcntl
import json
import os
import re
import sys
import time

VAULT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.realpath(__file__))))
ACTIVITY = os.path.join(VAULT, ".obsidian", "plugins", "graph-pulse", "activity.jsonl")
KEEP = 50
MAX_BYTES = 16 * 1024
MAX_TOKENS = 40
# Deckelt Dateisystemzugriffe (realpath, isfile), nicht bloss Tokens: Tokens
# kosten nichts, ein Zugriff auf dem DrvFs-Mount aber spuerbar etwas.
MAX_FS_OPS = 16

KIND = {
    "Read": "read",
    "NotebookRead": "read",
    "Edit": "write",
    "Write": "write",
    "NotebookEdit": "write",
    "Grep": "search",
    "Glob": "search",
}

# In auto mode files are read with cat and sed instead of the Read tool, so the
# bash command line is the only place those paths appear, absolute or relative.
BASH_WRITE = re.compile(r"(?:^|[|;&]\s*)(?:tee|cp|mv|sed\s+-i|rm)\b|>>?\s*\S")
BASH_CD = re.compile(r"\s*cd\s+(\S+)")
BASH_SPLIT = re.compile(r"&&|[|;]")
# Nur Tokens mit Slash oder Punkt sehen wie ein Pfad aus, alles andere (Befehle,
# Flags, blosse Woerter wie "projects") kostet keinen weiteren Schritt.
PATH_LIKE = re.compile(r"[/.]")
# Ein blankes Token davor ist reine Umleitung, das Token danach ist eindeutig
# das Schreibziel, egal ob die Datei schon existiert.
REDIRECT_OP = re.compile(r"\d{0,2}>{1,2}")


def clean(token):
    # expanduser hier, nicht erst in relative(), sonst zaehlt eine Tilde im
    # cd-Ziel oder einem Token nirgends als absoluter Pfad.
    return os.path.expanduser(token.strip().strip("'\"").rstrip(",;)"))


def literal_under_vault(path):
    """Guenstiger Vortest ohne Dateisystemzugriff.

    realpath kostet auf dem DrvFs-Mount messbar etwas (rund 20ms), bei vielen
    Kandidaten je Bash-Aufruf summiert sich das. Wer schon als reiner String
    nicht unter VAULT liegt, braucht nie bis dorthin geprueft zu werden.
    """
    normalized = os.path.normpath(path)
    return normalized == VAULT or normalized.startswith(VAULT + os.sep)


def to_rel(real, require_file):
    """Letzter, gemeinsamer Schritt: isfile pruefen, .obsidian ausschliessen."""
    if require_file and not os.path.isfile(real):
        return None
    rel = os.path.relpath(real, VAULT).replace(os.sep, "/")
    # Alles unter .obsidian hat keinen Knoten im Graphen, allen voran die
    # Aktivitaetsdatei dieses Hooks selbst. Der Vault-Ordner selbst (".") hat
    # ebenfalls keinen Knoten.
    if rel == "." or rel.startswith(".obsidian/"):
        return None
    return rel


def relative(path, require_file):
    """Loest einen absoluten Pfad zu einem Vault-relativen Knotennamen auf.

    Nur fuer absolute Tokens und strukturierte Werkzeuge: hier greift kein
    literal_under_vault-Vorfilter, also muss realpath den symlinkenden
    Memory-Ordner aufloesen, auch wenn das nach eigenem Text nicht unter
    VAULT liegt.
    """
    try:
        real = os.path.realpath(path)
    except OSError:
        return None
    if not real.startswith(VAULT + os.sep):
        return None
    return to_rel(real, require_file)


def relative_literal(path, require_file):
    """Schneller Zweig fuer Kandidaten, die literal_under_vault schon bestaetigt hat.

    realpath ist hier fuer die Zugehoerigkeit nicht mehr noetig, das hat der
    Vorfilter als reiner Stringvergleich schon geklaert, es bleibt hoechstens
    noch ein isfile. Ein Symlink innerhalb des Vaults wird dadurch nicht mehr
    aufgeloest, das ist hier bewusst in Kauf genommen.
    """
    return to_rel(os.path.normpath(path), require_file)


def resolve_bash_token(cleaned, bases, require_file, budget):
    if cleaned.startswith("/"):
        # Absolut, auch der symlinkende Memory-Ordner: nur hier darf realpath
        # ungeprueft ran, der guenstige Vortest greift erst nach dem Join.
        if budget[0] <= 0:
            return None
        budget[0] -= 1
        return relative(cleaned, require_file)
    for base in bases:
        candidate = os.path.join(base, cleaned)
        if not literal_under_vault(candidate):
            continue
        if require_file:
            if budget[0] <= 0:
                return None
            budget[0] -= 1
        rel = relative_literal(candidate, require_file)
        if rel:
            return rel
    return None


def bash_targets(command, cwd):
    kind = "write" if BASH_WRITE.search(command) else "read"

    # Ab dem ersten Heredoc-Marker ist der Rest Nutzdaten, keine Shell-Syntax
    # mehr, sonst wird Prosa im Dokument selbst als Pfad gelesen.
    heredoc = command.find("<<")
    scan = command if heredoc == -1 else command[:heredoc]

    cd_target = None
    for part in BASH_SPLIT.split(scan):
        match = BASH_CD.match(part)
        if match:
            cd_target = clean(match.group(1))

    # VAULT ist nur die Basis, wenn weder cd noch cwd etwas hergeben, sonst
    # matcht jedes blosse Wort mit Punkt (log.md als Suchmuster) zufaellig
    # gegen die Vault-Wurzel.
    bases = [b for b in (cd_target, cwd) if b] or [VAULT]

    tokens = scan.split()[:MAX_TOKENS]
    budget = [MAX_FS_OPS]
    single_q = 0
    double_q = 0
    result = []
    for i, token in enumerate(tokens):
        # Ein rohes Token direkt nach > oder >> ist unzweideutig das
        # Schreibziel, auch wenn die Datei durch genau diesen Aufruf erst
        # entsteht (echo/touch im Auto-Modus statt Write-Werkzeug). Aber nur,
        # wenn wir gerade nicht mitten in einer Zeichenkette stehen: die
        # Tokenisierung trennt nur an Leerzeichen und kennt kein Quoting,
        # ein > in Anfuehrungszeichen ist Text, keine echte Umleitung.
        forced_write = (
            i > 0
            and REDIRECT_OP.fullmatch(tokens[i - 1])
            and single_q % 2 == 0
            and double_q % 2 == 0
        )
        single_q += token.count("'")
        double_q += token.count('"')

        cleaned = clean(token)
        if not cleaned or cleaned.startswith("-") or not PATH_LIKE.search(cleaned):
            continue
        if cleaned in bases:
            # Das cd-Ziel oder cwd steht oft selbst als Token im Kommando, das
            # ist immer ein Verzeichnis und kostet sonst unnoetig Budget.
            continue
        rel = resolve_bash_token(cleaned, bases, not forced_write, budget)
        if rel:
            result.append((rel, kind))
    return result


def structured_targets(tool, data):
    kind = KIND.get(tool)
    if not kind:
        return []
    path = data.get("file_path") or data.get("notebook_path") or data.get("path")
    if not path:
        return []
    # Nur Write legt legitim eine neue Datei an, Edit und NotebookEdit setzen
    # trotz gleicher Art "write" eine existierende voraus.
    rel = relative(clean(path), require_file=tool != "Write")
    return [(rel, kind)] if rel else []


def targets(tool, data, cwd):
    if tool == "Bash":
        return bash_targets(data.get("command") or "", cwd)
    return structured_targets(tool, data)


def store(events):
    """Haengt die Ereignisse als je eine Zeile an.

    Mehrere Werkzeuge laufen parallel, also auch mehrere Hooks. Lesen, Aendern
    und Zurueckschreiben der ganzen Datei liesse dabei jedes Mal ein Ereignis
    verschwinden, deshalb eine Sperre um Anhaengen und Kappen.
    """
    fd = os.open(ACTIVITY, os.O_RDWR | os.O_CREAT, 0o644)
    with os.fdopen(fd, "r+", encoding="utf-8") as fh:
        fcntl.flock(fh.fileno(), fcntl.LOCK_EX)
        try:
            fh.seek(0, os.SEEK_END)
            for event in events:
                fh.write(json.dumps(event) + "\n")
            if fh.tell() > MAX_BYTES:
                fh.seek(0)
                lines = fh.readlines()[-KEEP:]
                fh.seek(0)
                fh.truncate()
                fh.writelines(lines)
        finally:
            fcntl.flock(fh.fileno(), fcntl.LOCK_UN)


def main():
    try:
        payload = json.load(sys.stdin)
    except (ValueError, OSError):
        return

    tool = payload.get("tool_name") or ""
    data = payload.get("tool_input") or {}
    if not isinstance(data, dict):
        return
    cwd = payload.get("cwd") or ""

    stamp = int(time.time() * 1000)
    events = []
    seen = set()
    for rel, kind in targets(tool, data, cwd):
        if rel in seen:
            continue
        seen.add(rel)
        events.append({"p": rel, "k": kind, "t": stamp})

    if events:
        store(events)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        pass
    sys.exit(0)
