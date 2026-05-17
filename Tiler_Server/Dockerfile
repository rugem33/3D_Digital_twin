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
