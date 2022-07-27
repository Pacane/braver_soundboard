#!/usr/bin/env bash

ls lavalink/assets > assets.txt

docker build --platform=linux/amd64 -t braver-bot:latest-amd64 -f Dockerfile .
docker tag braver-bot northamerica-northeast1-docker.pkg.dev/stacktrace-295302/pacane/braver-bot:latest-amd64
docker push northamerica-northeast1-docker.pkg.dev/stacktrace-295302/pacane/braver-bot-amd64