"""Builds INSTALL.md from install/INSTALL.template.md, inserting the Claude Code prompts word for word.

Usage: python3 tools/make-install-guide.py
"""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
text = (root / "install/INSTALL.template.md").read_text()
text = text.replace("{{PROMPT_MAC}}", (root / "install/prompt-mac.txt").read_text().rstrip("\n"))
text = text.replace("{{PROMPT_WINDOWS}}", (root / "install/prompt-windows.txt").read_text().rstrip("\n"))
(root / "INSTALL.md").write_text(text)
print("wrote INSTALL.md")
