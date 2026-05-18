# ────── Stage 1: ctb-tile 컴파일 ──────────────────────────────────
FROM python:3.11-slim-bookworm AS ctb-builder

RUN sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/debian.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        cmake \
        git \
        gdal-bin \
        libgdal-dev \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 https://github.com/geo-data/cesium-terrain-builder /src/ctb \
    && cd /src \
    && apt-get update \
    && apt-get source gdal \
    && cp "$(find /src/gdal-* -iname '*overview*dataset*.cpp' -print -quit)" /src/ctb/src/gdaloverviewdataset.cpp \
    && sed -i '/#include "GDALTiler.hpp"/a #include "gdaloverviewdataset.cpp"' /src/ctb/src/GDALTiler.cpp \
    && grep -q '#include "gdaloverviewdataset.cpp"' /src/ctb/src/GDALTiler.cpp \
    && cmake -S /src/ctb -B /src/ctb/build -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /src/ctb/build -j$(nproc) \
    && cmake --install /src/ctb/build --prefix /usr/local

# ────── Stage 2: 최종 이미지 ──────────────────────────────────────
FROM python:3.11-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    LD_LIBRARY_PATH=/usr/local/lib

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        gdal-bin \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ctb-builder /usr/local /usr/local
RUN ldconfig && command -v ctb-tile

RUN pip install --no-cache-dir flask

WORKDIR /app

COPY Tools/terrain_server.py ./terrain_server.py
COPY Tools/process_dem.py    ./process_dem.py

RUN mkdir -p /app/tiles /app/_uploads /app/_work

EXPOSE 5001

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:5001/health', timeout=3)"

CMD ["python", "terrain_server.py"]
