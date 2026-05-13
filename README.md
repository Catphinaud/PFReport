# PFReport

Dalamud plugin for catching party finder listings that look reportable.

It has a small rules list, a report text template, and a captured listings window so you can copy stuff faster instead of manually rewriting the same report every time.

## logging thing

There is also opt-in party finder logging now.

It is off by default. If you turn it on and set an ingest url, the plugin can send party finder listings to the small server in `server/`.

The point of that is mostly collecting descriptions for nosol plugin training / spam filtering later.

Logged fields are kept boring on purpose:

```text
name
home world
description
search area
duty id
minilv
```

The client keeps an in-memory hash cache so it does not keep sending the same listing over and over. The server also dedupes by hash and stores rows in a local BoltDB file.

## commands

```text
/pfreport
/pfreport status
/pfreport enable
/pfreport disable 5m
/pfreport disable restart
/pfreport disable logout
```

## server

```bash
cd server
go run .
```

Then open:

```text
http://127.0.0.1:8787/
```

Plugin logging url is normally:

```text
http://127.0.0.1:8787/v1/listings
```

See `server/README.md` for the server routes.
