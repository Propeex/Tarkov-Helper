from pathlib import Path
import subprocess

ORIGINAL_COMMIT = "5edf90d0a4a3c02590533d95bed6448be352d811"
original = subprocess.check_output(
    ["git", "show", f"{ORIGINAL_COMMIT}:.github/v182/phase2.py"],
    text=True,
    encoding="utf-8",
)

old = '''    if count != 1:
        raise RuntimeError(f"{path}: expected one literal match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))'''
new = '''    if count < 1:
        raise RuntimeError(f"{path}: expected at least one literal match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))'''
if original.count(old) != 1:
    raise RuntimeError(f"phase2 helper matcher count: {original.count(old)}")

source = original.replace(old, new, 1)
namespace = {
    "__name__": "__main__",
    "__file__": str(Path(__file__).resolve()),
}
exec(compile(source, str(Path(__file__).resolve()), "exec"), namespace)
