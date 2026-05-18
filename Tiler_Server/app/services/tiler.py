import subprocess
from dataclasses import dataclass
from pathlib import Path

from app import config


@dataclass(frozen=True)
class TilerOptions:
    coordinate_code: str = config.DEFAULT_COORDINATE_CODE
    height_column: str = config.DEFAULT_HEIGHT_COLUMN
    skirt_height: str = config.DEFAULT_SKIRT_HEIGHT
    input_type: str = config.DEFAULT_INPUT_TYPE
    output_type: str = config.DEFAULT_OUTPUT_TYPE
    curvature_correction: bool = True


@dataclass(frozen=True)
class TilerResult:
    command: list[str]
    stdout: str
    stderr: str
    tileset_path: Path


def build_mago_command(
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None = None,
) -> list[str]:
    command = [
        "java",
        f"-Xms{config.JAVA_XMS}",
        f"-Xmx{config.JAVA_XMX}",
        "-jar",
        str(config.MAGO_TILER_JAR),
        "-i",
        str(input_dir),
        "-o",
        str(output_dir),
        "-it",
        options.input_type,
        "-ot",
        options.output_type,
        "-c",
        options.coordinate_code,
        "-hc",
        options.height_column,
        "-sh",
        options.skirt_height,
    ]

    if options.curvature_correction:
        command.append("-cc")

    if terrain_path:
        command.extend(["-te", str(terrain_path)])

    return command


def run_mago_tiler(
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None = None,
) -> TilerResult:
    if not config.MAGO_TILER_JAR.exists():
        raise FileNotFoundError(
            f"mago3d tiler jar not found: {config.MAGO_TILER_JAR}. "
            "Mount or copy mago-3d-tiler-1.15.4.jar into vendor/."
        )

    output_dir.mkdir(parents=True, exist_ok=True)
    command = build_mago_command(input_dir, output_dir, options, terrain_path)

    completed = subprocess.run(
        command,
        capture_output=True,
        text=True,
        timeout=config.CONVERT_TIMEOUT_SECONDS,
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            "mago3d tiler failed\n"
            f"command: {' '.join(command)}\n"
            f"stdout:\n{completed.stdout}\n"
            f"stderr:\n{completed.stderr}"
        )

    tileset_path = find_tileset(output_dir)
    return TilerResult(command, completed.stdout, completed.stderr, tileset_path)


def find_tileset(output_dir: Path) -> Path:
    candidates = sorted(output_dir.rglob("tileset.json"))
    if not candidates:
        raise FileNotFoundError(f"tileset.json was not generated under {output_dir}")
    return candidates[0]
