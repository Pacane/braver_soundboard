#!/usr/bin/env bash

ls lavalink/assets > assets.txt

docker build -t braver-bot -f Dockerfile .
docker tag braver-bot northamerica-northeast1-docker.pkg.dev/stacktrace-295302/pacane/braver-bot
docker push northamerica-northeast1-docker.pkg.dev/stacktrace-295302/pacane/braver-bot