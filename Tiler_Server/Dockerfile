FROM python:3.11-slim AS ctb-builder

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

FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    APP_BASE_DIR=/app \
    DATA_DIR=/data \
    MAGO_TILER_JAR=/opt/mago/mago-3d-tiler-1.15.4.jar

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        openjdk-21-jre-headless \
        gdal-bin \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ctb-builder /usr/local /usr/local
RUN ldconfig && command -v ctb-tile

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app
COPY tools ./tools
COPY scripts ./scripts

RUN mkdir -p /data/uploads /data/outputs /data/terrain_tiles /opt/mago
COPY vendor/mago-3d-tiler-1.15.4.jar /opt/mago/mago-3d-tiler-1.15.4.jar

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=3)"

CMD ["gunicorn", "--bind", "0.0.0.0:8000", "--workers", "2", "--timeout", "1800", "app.main:app"]
