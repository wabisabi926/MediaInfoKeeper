#!/usr/bin/env python3
import hashlib
import os
import subprocess
import sys
import tempfile
from pathlib import Path


def build_runner(cache_root: Path) -> Path:
    project_dir = cache_root / "assembly-version-reader"
    project_dir.mkdir(parents=True, exist_ok=True)

    csproj = project_dir / "AssemblyVersionReader.csproj"
    program = project_dir / "Program.cs"

    csproj.write_text(
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""",
        encoding="utf-8",
    )

    program.write_text(
        """using System.Reflection;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: read_assembly_version.py <assembly-path>");
    return 1;
}

try
{
    var version = AssemblyName.GetAssemblyName(args[0]).Version;
    if (version is null)
    {
        Console.Error.WriteLine("Assembly version not found.");
        return 1;
    }

    Console.WriteLine(version);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to read assembly version: {ex.Message}");
    return 1;
}
""",
        encoding="utf-8",
    )

    subprocess.run(
        ["dotnet", "build", str(csproj), "-nologo", "-clp:ErrorsOnly"],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
        cwd=project_dir,
    )

    return project_dir / "bin" / "Debug" / "net10.0" / "AssemblyVersionReader.dll"


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: read_assembly_version.py <assembly-path>", file=sys.stderr)
        return 1

    assembly_path = Path(sys.argv[1]).expanduser().resolve()
    if not assembly_path.is_file():
        print(f"Assembly not found: {assembly_path}", file=sys.stderr)
        return 1

    cache_key = hashlib.sha256(b"assembly-version-reader-v1").hexdigest()[:12]
    cache_root = Path(tempfile.gettempdir()) / f"mediainfokeeper-{cache_key}"

    try:
        runner = build_runner(cache_root)
        completed = subprocess.run(
            ["dotnet", str(runner), str(assembly_path)],
            check=False,
            capture_output=True,
            text=True,
        )
    except subprocess.CalledProcessError as exc:
        print(exc.stderr.strip() or str(exc), file=sys.stderr)
        return 1

    if completed.returncode != 0:
        print(completed.stderr.strip() or "Failed to read assembly version.", file=sys.stderr)
        return completed.returncode

    sys.stdout.write(completed.stdout.strip())
    if completed.stdout and not completed.stdout.endswith("\n"):
      sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
