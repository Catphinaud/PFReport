# Party finder logging server

Small server for party finder logging.

This is not meant to be fancy. It takes the listings the plugin sends, dedupes them by hash, and keeps them in a local BoltDB file.

## running it

```bash
cd server
go run .
```

By default it listens on `:8787` and writes to `data/pfreport.db`.

Useful env vars:

```bash
PFREPORT_ADDR=:8787
PFREPORT_DATA=data/pfreport.db
PFREPORT_TOKEN=some-secret-token
PFREPORT_MAX_BODY_BYTES=1048576
```

If `PFREPORT_TOKEN` is set, put the same token in the plugin config. It uses `Authorization: Bearer <token>`.

## dashboard

Open this in a browser:

```text
http://127.0.0.1:8787/
```

It shows recent rows and has a search box. If token auth is on, paste the token in the token box.

## routes

- `GET /`
    - web dashboard

- `GET /health`
    - basic health check

- `GET /v1/routes`
    - prints the routes as json

- `GET /v1/stats`
    - counters and some db info

- `GET /v1/recent?page=1&perPage=100&q=spam`
    - recent rows as json
    - old `limit=100` still works too
    - `perPage` max is 2000
    - `q` searches name/world/hash/duty/minilv/area/description/source

- `POST /v1/listings`
    - main ingest endpoint for the plugin
    - stores rows that have not been seen before
    - dupes are skipped by hash

- `POST /v1/test`
    - test endpoint
    - returns ok but does not store anything

- `GET /v1/export`
    - dumps stored rows as ndjson

- `GET /v1/export?format=csv`
    - dumps csv

- `GET /v1/export?format=csv&gzip=1`
    - dumps csv.gz
    - useful for later scripts / ml data stuff

## test

```bash
cd server
go test ./...
```

If go complains about cache paths in a weird env, this works:

```bash
GOCACHE=/tmp/go-build go test ./...
```
