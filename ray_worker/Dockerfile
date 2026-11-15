FROM python:3.11-slim

WORKDIR /ray_app

RUN apt-get update && apt-get install -y --no-install-recommends curl procps && rm -rf /var/lib/apt/lists/*

COPY ray_worker/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY ray_worker/app.py ./app.py

EXPOSE 6379 8266
