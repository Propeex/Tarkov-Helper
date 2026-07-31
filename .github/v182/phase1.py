from pathlib import Path
import subprocess

ORIGINAL_COMMIT = "f213d6691bf68ccca1408757a06b0852851cdffc"
original = subprocess.check_output(
    ["git", "show", f"{ORIGINAL_COMMIT}:.github/v182/phase1.py"],
    text=True,
    encoding="utf-8",
)

old = '''    "                            Content=\\"{Binding ActionButtonText}\\" Padding=\\"8,4\\"",
    "                            Content=\\"완료\\" Padding=\\"8,4\\"",'''
new = '''    "                    <Button Grid.Column=\\"4\\" Content=\\"{Binding ActionButtonText}\\" Padding=\\"8,4\\"",
    "                    <Button Grid.Column=\\"4\\" Content=\\"완료\\" Padding=\\"8,4\\"",'''
if original.count(old) != 1:
    raise RuntimeError(f"phase1 source matcher count: {original.count(old)}")

source = original.replace(old, new, 1)
namespace = {
    "__name__": "__main__",
    "__file__": str(Path(__file__).resolve()),
}
exec(compile(source, str(Path(__file__).resolve()), "exec"), namespace)
