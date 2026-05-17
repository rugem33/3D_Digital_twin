#!/usr/bin/env python3
"""
한국 지형 DEM 자동 다운로드 (Copernicus DEM 30m, 무료/무인증)

데이터: ESA Copernicus GLO-30 (AWS S3 공개 버킷, 인증 불필요)
해상도: 30m (1°×1° 타일)
커버리지: 한국 전역 (124-132°E, 33-39°N)

사용법:
    python Tools/download_dem.py               # 다운로드만
    python Tools/download_dem.py --process     # 다운로드 + 타일 변환까지
    python Tools/download_dem.py --max-zoom 14 # 고해상도 타일
    python Tools/download_dem.py --check       # 기존 다운로드 상태만 확인
"""

import argparse
import struct
import subprocess
import sys
import urllib.request
import urllib.error
from pathlib import Path

TOOLS_DIR = Path(__file__).parent
DEM_DIR   = TOOLS_DIR / "_dem_tiles"
WORK_DIR  = TOOLS_DIR / "_work"

# Copernicus DEM GLO-30 AWS S3 공개 버킷
# URL: https://copernicus-dem-30m.s3.amazonaws.com/{folder}/{file}.tif
COPERNICUS_BASE = "https://copernicus-dem-30m.s3.amazonaws.com"

# 한국 영역 (여유 포함)
KOREA_LAT = range(33, 39)   # N33 ~ N38
KOREA_LON = range(124, 132) # E124 ~ E131


def copernicus_url(lat: int, lon: int) -> tuple[str, str]:
    """Copernicus DEM 타일 URL과 파일명 반환."""
    ns = "N" if lat >= 0 else "S"
    ew = "E" if lon >= 0 else "W"
    name = f"Copernicus_DSM_COG_10_{ns}{abs(lat):02d}_00_{ew}{abs(lon):03d}_00_DEM"
    url  = f"{COPERNICUS_BASE}/{name}/{name}.tif"
    return url, f"{name}.tif"


def download_tile(lat: int, lon: int, dest_dir: Path) -> Path | None:
    """타일 1개 다운로드. 없거나 실패하면 None."""
    url, fname = copernicus_url(lat, lon)
    out = dest_dir / fname
    if out.exists() and out.stat().st_size > 10_000:
        return out  # 이미 존재

    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            if resp.status != 200:
                return None
            data = resp.read()
        out.write_bytes(data)
        return out
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None   # 해당 위치에 타일 없음 (해양 등)
        print(f"   HTTP {e.code}: {url}")
        return None
    except Exception as e:
        print(f"   오류: {e}")
        return None


def check_existing(dest_dir: Path) -> list[Path]:
    return sorted(dest_dir.glob("Copernicus_*.tif"))


def merge_tiles(tile_paths: list[Path], output: Path):
    """gdalbuildvrt + gdal_translate 로 타일 합치기."""
    if not tile_paths:
        print("[ERROR] 합칠 타일 없음")
        sys.exit(1)

    vrt = WORK_DIR / "merged.vrt"
    WORK_DIR.mkdir(exist_ok=True)

    tile_list_str = " ".join(f'"{p}"' for p in tile_paths)
    ret = subprocess.run(
        f'gdalbuildvrt -srcnodata -9999 "{vrt}" {tile_list_str}',
        shell=True, capture_output=True
    )
    if ret.returncode != 0:
        # GDAL 없을 때 단순 복사
        print("[경고] gdalbuildvrt 실패 — 첫 번째 타일을 단독으로 사용합니다.")
        import shutil
        shutil.copy(tile_paths[0], output)
        return

    ret2 = subprocess.run(
        f'gdal_translate -of GTiff -a_nodata -9999 "{vrt}" "{output}"',
        shell=True, capture_output=True
    )
    if ret2.returncode != 0:
        import shutil
        shutil.copy(tile_paths[0], output)
        return

    print(f"[병합] → {output}  ({output.stat().st_size//1024//1024}MB)")


def simple_merge_tiff(tile_paths: list[Path], output: Path):
    """
    GDAL 없이 gdal_merge.py 또는 Python으로 단순 처리.
    타일이 1개면 그냥 복사, 여러 개면 gdal_merge.py 시도.
    """
    import shutil
    if len(tile_paths) == 1:
        shutil.copy(tile_paths[0], output)
        print(f"[복사] {tile_paths[0].name} → {output.name}")
        return

    # gdal_merge.py (GDAL Python 스크립트, conda/OSGeo4W에 포함)
    tile_args = " ".join(f'"{p}"' for p in tile_paths)
    ret = subprocess.run(
        f'gdal_merge.py -o "{output}" -n -9999 -a_nodata -9999 {tile_args}',
        shell=True, capture_output=True
    )
    if ret.returncode == 0:
        print(f"[병합] gdal_merge.py 완료 → {output}")
    else:
        shutil.copy(tile_paths[0], output)
        print(f"[경고] 병합 실패, 첫 타일만 사용: {tile_paths[0].name}")


def verify_dem(dem_path: Path):
    """생성된 DEM의 기본 정보 출력."""
    with open(dem_path, "rb") as f:
        magic = f.read(4)
    if magic[:2] != b"II" and magic[:2] != b"MM":
        print("[경고] 유효한 GeoTIFF가 아닙니다.")
        return

    size_mb = dem_path.stat().st_size / 1024 / 1024
    print(f"[확인] {dem_path.name}  크기: {size_mb:.0f}MB")


def main():
    parser = argparse.ArgumentParser(
        description="Copernicus DEM 30m 한국 자동 다운로드",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--bounds", type=float, nargs=4,
                        metavar=("LON_MIN", "LAT_MIN", "LON_MAX", "LAT_MAX"),
                        help="다운로드 범위 (기본: 한국 124 33 132 39)")
    parser.add_argument("--process",  action="store_true",
                        help="다운로드 후 process_dem.py 자동 실행")
    parser.add_argument("--max-zoom", type=int, default=12,
                        help="타일 생성 최대 줌 레벨 (--process 사용 시)")
    parser.add_argument("--check",    action="store_true",
                        help="기존 다운로드 상태만 출력")
    args = parser.parse_args()

    if args.bounds:
        lon_min, lat_min, lon_max, lat_max = args.bounds
        lat_range = range(int(lat_min), int(lat_max))
        lon_range = range(int(lon_min), int(lon_max))
    else:
        lat_range = KOREA_LAT
        lon_range = KOREA_LON

    DEM_DIR.mkdir(exist_ok=True)

    # ── 상태 확인 모드 ──────────────────────────────────────
    if args.check:
        existing = check_existing(DEM_DIR)
        print(f"기존 다운로드: {len(existing)}개")
        for p in existing:
            print(f"  {p.name}  ({p.stat().st_size//1024}KB)")
        return

    # ── 다운로드 ───────────────────────────────────────────
    total = len(lat_range) * len(lon_range)
    print(f"Copernicus DEM 30m 다운로드 시작")
    print(f"대상: {len(lat_range)}×{len(lon_range)} = {total}타일 (해양 타일은 자동 스킵)")
    print(f"저장: {DEM_DIR}\n")

    downloaded: list[Path] = []
    skipped = 0

    for lat in lat_range:
        for lon in lon_range:
            url, fname = copernicus_url(lat, lon)
            out = DEM_DIR / fname
            if out.exists() and out.stat().st_size > 10_000:
                downloaded.append(out)
                continue

            ns = "N" if lat >= 0 else "S"
            ew = "E" if lon >= 0 else "W"
            print(f"  {ns}{abs(lat):02d} {ew}{abs(lon):03d}  ", end="", flush=True)
            result = download_tile(lat, lon, DEM_DIR)
            if result:
                size_kb = result.stat().st_size // 1024
                print(f"OK ({size_kb}KB)")
                downloaded.append(result)
            else:
                print("스킵 (해양/없음)")
                skipped += 1

    print(f"\n다운로드: {len(downloaded)}개  스킵: {skipped}개")

    if not downloaded:
        print("[ERROR] 다운로드된 DEM 타일이 없습니다.")
        print("        네트워크 연결 또는 AWS S3 접근을 확인하세요.")
        sys.exit(1)

    # ── 병합 ───────────────────────────────────────────────
    WORK_DIR.mkdir(exist_ok=True)
    merged = WORK_DIR / "korea_dem.tif"

    print(f"\n[병합] {len(downloaded)}개 타일 합치는 중...")
    import shutil as _shutil
    if _shutil.which("gdalbuildvrt"):
        merge_tiles(downloaded, merged)
    else:
        simple_merge_tiff(downloaded, merged)

    verify_dem(merged)

    # ── 타일 생성 ──────────────────────────────────────────
    if args.process:
        print(f"\n[process_dem.py] 타일 변환 시작...")
        script = TOOLS_DIR / "process_dem.py"
        bounds_arg = ""
        if args.bounds:
            lon_min, lat_min, lon_max, lat_max = args.bounds
            bounds_arg = f"--bounds {lon_min} {lat_min} {lon_max} {lat_max}"
        ret = subprocess.run(
            f'python "{script}" --input "{merged}" --nodata -9999 '
            f'--max-zoom {args.max_zoom} {bounds_arg}',
            shell=True
        )
        if ret.returncode != 0:
            print("[ERROR] 타일 변환 실패")
            sys.exit(1)
    else:
        print(f"""
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  다음 단계: 타일 변환
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  python Tools/process_dem.py \\
      --input "{merged}" \\
      --nodata -9999 \\
      --max-zoom 12

  또는 한 번에:
  python Tools/download_dem.py --process --max-zoom 12
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
""")


if __name__ == "__main__":
    main()
